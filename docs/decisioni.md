# Decisioni

Scelte che sembrano sbagliate finché non se ne conosce la ragione. Sono qui
perché il rischio non è che vengano ignorate, ma che qualcuno le "semplifichi"
riportandole alla forma ovvia.

---

## D-1 — La selezione pagina a chiave, non con `TOP` più `NOT EXISTS`

**Contesto.** Le selezioni erano un unico `INSERT..SELECT` sull'intera tabella
`Order`: transazione implicita lunga, crescita del log, lock prolungati.

**Decisione.** Ogni pagina riparte da `(@LastAnchor, @LastId)`, cioè dall'ultima
riga esaminata, ordinando sulla stessa coppia.

**Perché non il loop ovvio.** La forma naturale sarebbe ripetere
`TOP (N) ... WHERE NOT EXISTS (staging)` finché non inserisce più nulla. È
sbagliata per due motivi che si sommano:

1. Ogni iterazione riparte dall'inizio della tabella e scarta con l'anti-join le
   righe già prese. L'iterazione *k* ne salta *k×N*: costo complessivo
   quadratico. Su milioni di righe è **più lento** dello statement unico che
   sostituisce.
2. Il predicato che guida la scansione non è più la soglia sulla data, quindi il
   piano abbandona l'indice filtrato di `002_indexes.sql`. Una seek su una
   frazione della tabella diventa una scansione completa, ripetuta a ogni giro.

La coppia `(data, Id)` è esattamente la chiave di quegli indici: la colonna data
più la chiave di clustering che SQL Server accoda a ogni indice non
clusterizzato. Il costo totale resta una passata sola, spezzata in transazioni
corte.

**Conseguenze.**

- Il `NOT EXISTS` sullo staging resta, ma cambia ruolo: non è più il meccanismo
  di avanzamento, è la guardia di idempotenza per la riesecuzione.
- La selezione non è più atomica. Va bene: l'unico consumatore è la fase
  successiva dello stesso run, che non parte prima della fine del ciclo.
- La filigrana vive in memoria, quindi una ripresa dopo interruzione ricomincia
  dalla prima pagina. È una riscansione dell'indice, non una riselezione, e la
  si paga solo dopo un'interruzione. Persisterla su `PurgeRun` è possibile e
  costa una migrazione; ha senso solo se le selezioni risultano lunghe.
- Ogni selezione ha bisogno di un indice la cui chiave inizi con la colonna di
  ancoraggio. È la ragione di `IX_StandingOrder_Purge`.

**Se un giorno va rifatta.** Il segnale da guardare non è il tempo totale ma il
numero di pagine nel log di selezione confrontato con le righe esaminate: se le
righe esaminate crescono più che linearmente rispetto a quelle inserite, la
filigrana non sta agganciando l'indice.

---

## D-2 — L'audit si scrive dentro la transazione della slice

**Decisione.** Le righe di `Purge.PurgeAudit` vengono scritte da `SliceExecutor`
nella stessa `SqlTransaction` delle `DELETE`, non da un percorso separato a
valle.

**Perché.** Un audit fuori transazione può registrare cancellazioni che il
rollback poi annulla. È l'unico modo in cui un audit fa danno attivo: non dice
meno del vero, dice il falso, e chi lo legge non ha modo di accorgersene.

**Conseguenze.**

- Non esiste una seconda via per popolare la tabella. `RecordAuditAsync` è stata
  rimossa da `PurgeRunStore` apposta: lasciarla avrebbe reso disponibile il modo
  sbagliato.
- L'audit è append-only, con una riga per `(RunId, BatchNo, tabella)`. Nessun
  `UPDATE`, quindi nessun punto di contesa se un giorno le slice verranno
  eseguite in parallelo. L'aggregazione per run la fa la view.
- Un solo `INSERT` multi-riga prima del commit. La transazione tiene lock sulle
  tabelle di dominio: allungarla di venticinque andate e ritorno vanificherebbe
  il lavoro fatto sul dimensionamento delle slice.
- `PurgeAudit` cresce di qualche riga per slice e non viene mai sfoltita da
  `PurgeHousekeeping`. È corretto — è la traccia — ma il commento in quella
  classe che la definisce "piccola" andrà rivisto con numeri reali.

---

## D-3 — I collettivi non paginano

**Decisione.** `CollectiveStrategy` resta su statement singoli mentre le altre
quattro strategie paginano.

**Perché.** La selezione collettiva ha invarianti che attraversano l'intero
insieme: `ValidateOrderBelongsToSingleCollective` verifica che nessun ordine
appartenga a due collettivi eleggibili, e `SelectCollectiveComponents` deve
vedere tutti i collettivi selezionati. Paginare significa decidere cosa voglia
dire quell'invariante su una selezione ancora incompleta, ed è la decisione da
cui dipende l'atomicità dell'intero disegno. Va presa a parte, non come effetto
collaterale di un intervento sulle prestazioni.

La popolazione dei collettivi è inoltre di un altro ordine di grandezza rispetto
a quella degli ordini, quindi il problema che la paginazione risolve qui non si
pone con la stessa urgenza.

**Quando riaprirla.** Se il conteggio dei collettivi eleggibili in un run
comincia ad avvicinarsi a quello degli ordini.

---

## D-4 — Il dry-run gira con utenza in sola lettura

**Decisione precedente a questa versione, registrata qui perché è la più facile
da annullare per comodità.**

Per il dry-run non serve il permesso di `DELETE` su `PaymentOrder`. Eseguirlo
con un'utenza che non ce l'ha rende l'assenza di cancellazioni una proprietà
strutturale invece che una promessa del codice. Il `GRANT DELETE` va concesso
solo dopo l'approvazione del report.

`Purge:DryRun` resta legato al solo file di configurazione, il che è il punto
debole di questa decisione: una modifica fatta in sviluppo che finisse in
produzione farebbe cancellare davvero al primo run notturno. Legarlo
all'ambiente è ancora da fare.

---

## D-5 — Un guasto non cambia la fase del run

**Decisione.** Quando il run si interrompe per un guasto dell'ambiente, la
colonna `Phase` resta dov'è. L'interruzione viene registrata in
`InterruptionCount` e `LastInterruptedOn`.

**Perché non una fase `Interrupted`.** Era l'idea di partenza ed è sbagliata: la
fase *è* il checkpoint. Scrivere `Interrupted` in quella colonna cancella
l'informazione su quale fase fosse in corso, e la ripresa non sa più da dove
ripartire. Servirebbe una seconda colonna per ricordarla, cioè due campi per
rappresentare uno stato, con la possibilità che divergano.

Lasciare la fase intatta è anche ciò che il codice già faceva per la
cancellazione da chiusura finestra, e per la stessa ragione.

**Conseguenze.**

- Un run interrotto è indistinguibile, guardando la sola `Phase`, da un run in
  corso. `LastInterruptedOn` risponde alla domanda "da quando è fermo".
- Serve un limite alle riprese, altrimenti un guasto stabile riprova per sempre.
  È `MaxRunInterruptions`, oltre il quale il run diventa `Failed` con il motivo.
- La classificazione guasto/difetto è in `SqlErrors` più tre tipi di eccezione, e
  nel dubbio si classifica come difetto. Un difetto trattato come guasto viene
  riprovato all'infinito; un guasto trattato come difetto chiude un run che si
  sarebbe potuto riprendere. Il secondo errore è rumoroso, il primo silenzioso.

---

## D-6 — Gli abbandoni si contano dal database

**Decisione.** `ExecutingPhase` chiede a `PurgeRunStore` quante slice del run
sono abbandonate, invece di leggere `BatchExecutionResult.AbandonedSlices`.

**Perché.** Quel contatore vale per l'invocazione corrente. Un run che abbandona
due slice la prima notte, si ferma alla chiusura della finestra e completa il
resto la seconda, chiuderebbe con zero abbandoni e perderebbe l'informazione
proprio nel caso in cui serve.

**Dove sta il conteggio.** In `IBatchWorkProvider`, non in `ExecutingPhase`.
Il primo tentativo dava alla fase una dipendenza da `PurgeRunStore`, e questo
trasformava quattro test unitari in test che avevano bisogno di un database.
`BatchExecutionCoordinatorContractTests` verifica per riflessione che il
coordinatore non conosca `PurgeRunStore`: quel vincolo era già scritto, e la
prima versione lo aggirava passando dalla fase.

Il risultato porta quindi due numeri: `AbandonedSlices` per la sessione, che
serve al log, e `AbandonedTotal` per il run, che decide la fase finale.

**Conseguenza.** Una query in più per run, alla fine di `Executing`.

---

## D-7 — Contesa e guasto seguono due strade diverse

**Decisione.** In `SliceExecutor` i `catch` sono quattro e l'ordine conta:

1. `SqlException` di **concorrenza** (1205, 1222, -2) → `SliceResult.Retryable`.
   La stessa slice si riprova fra qualche secondo.
2. `OperationCanceledException` → rollback e rilancio. Finestra chiusa.
3. `SqlException` **transitoria ma non di concorrenza** → rollback e **rilancio**.
   Connessione caduta: il run si interrompe e resta riprendibile.
4. Tutto il resto → `SliceResult.Fatal`. La slice viene abbandonata.

**Perché il terzo `catch` deve esistere.** È il caso che si dimentica. La prima
versione classificava con `IsTransient` al punto 1, quindi una connessione caduta
faceva riprovare la slice fino a esaurire i tentativi e poi l'abbandonava.
Correggere il punto 1 in `IsConcurrency` senza aggiungere il punto 3 è peggio: la
`SqlException` cade nel `catch` generico e diventa `Fatal`, cioè viene
abbandonata **subito**, senza nemmeno i tre tentativi.

In entrambi i casi il risultato è lo stesso: aggregati lasciati a database in via
definitiva, e un run chiuso in `CompletedWithErrors`, per un guasto che sarebbe
passato da solo.

**La catena.** Il rilancio funziona perché nessuno lo intercetta per strada:
`BatchExecutionCoordinator` non ha `try`, `ExecutingPhase` nemmeno, e
`RetentionOrchestrator` lo riconosce con `IsInfrastructure` e registra
un'interruzione senza cambiare fase. Modificare uno qualsiasi di questi tre punti
aggiungendo una cattura rompe la proprietà senza che nessun test attuale se ne
accorga, tranne
`BatchExecutionCoordinatorTests.Executor_exception_propagates_without_abandoning`.

**Cosa non è ancora verificato.** La classificazione è coperta da
`SqlErrorsTests`, la propagazione dal coordinatore in su dal test appena citato,
e il comportamento dell'orchestratore da `RunLifecycleTests`. Manca l'anello
`SliceExecutor` stesso: non è isolabile finché `SqlExecutor` resta una classe
concreta. È la ragione principale per introdurre `ISqlExecutor`.

**Proprietà utile scoperta strada facendo.** Se il guasto colpisce
`tx.CommitAsync`, lo stato è ambiguo: il commit può essere passato o no. Non è un
problema perché il checkpoint di slice sta **dentro** la transazione. Se il
commit è passato, la slice risulta `Completed` e non viene ripresa; se non è
passato, resta `Pending`. Le due possibilità portano allo stesso comportamento
corretto senza bisogno di distinguerle.

---

## D-8 — Il planning legge i candidati in due passate, non in una

**Contesto.** Il `BatchPlanner` teneva un `DataReader` aperto per tutta la
lettura dei candidati e faceva `SqlBulkCopy` sulla stessa connessione a ogni
50.000 righe. Senza MARS quella combinazione non è ammessa: la prima
esecuzione con più di 50.000 candidati sarebbe fallita, cioè la prima
esecuzione reale. La correzione è passare a pagine, chiudendo il reader prima
di ogni flush.

**Decisione.** Le pagine sono due sequenze keyset distinte — prima gli
standalone su `OrderId`, poi i componenti dei collettivi su
`(CollectiveOrderId, OrderId)` — e non un unico statement ordinato per
`CASE WHEN CollectiveOrderId IS NULL THEN 0 ELSE 1 END, CollectiveOrderId, OrderId`.

**Perché non il loop ovvio.** L'ordinamento con il `CASE` produce esattamente
la stessa sequenza ed è la forma naturale, ma il `CASE` non è sargable: nessun
indice può soddisfare quell'`ORDER BY`, quindi **ogni pagina** paga un sort del
residuo. Con N candidati e pagine da P il costo diventa N²/P, cioè la stessa
trappola descritta in D-1, spostata dalla selezione al planning. Le due passate
sono invece seek su chiave: la prima è servita dalla PK cluster
`(RunId, OrderId)` senza alcun sort.

Nota su SQL Server: in `ORDER BY` ascendente i NULL vengono per primi, quindi
il `CASE` era anche ridondante — non stava ordinando, stava solo impedendo
l'uso dell'indice.

**Conseguenze.**

- La contiguità dei componenti di un collettivo, che è il contratto del
  `BatchPacker`, ora è garantita dalla seconda passata invece che da un
  ordinamento globale. Il `BatchPacker` sopravvive al confine di pagina: è
  quello che rende il caso testabile solo con il database.
- Per le strategie diverse da `Collective` la seconda passata non restituisce
  nulla. Resta comunque incondizionata: è una scansione sola, e non vogliamo
  che la correttezza del planning dipenda da un'invariante che vive nelle query
  di selezione.
- Il sentinella è `Guid.Empty`, come in `ExpandAndWeigh`. Un ramo
  `@LastId IS NULL OR ...` avrebbe reso il predicato di nuovo non sargable.
- Il ciclo esce sulla pagina incompleta, non sulla pagina vuota: una query in
  meno per passata.

**Se un giorno va rifatta.** Il segnale è nel piano di esecuzione della seconda
passata: se compare un operatore Sort su volumi che contano, l'indice da
valutare è `(RunId, State, CollectiveOrderId, OrderId) INCLUDE (RowWeight)`.
Va deciso con un piano alla mano, non per precauzione: è un indice in più da
mantenere su una tabella che cresce in proporzione ai dati cancellati.

---

## D-9 — La finestra operativa ferma anche le fasi che non sono l'esecuzione

**Contesto.** `IsWithinWindow` era controllato in `BatchExecutionCoordinator` e
in `PurgeHousekeeping`, cioè nelle due parti che lavorano a lotti. `Selecting`,
`Expanding`, `Validating` e `Planning` non lo controllavano.
`BatchedStatementRunner` dichiarava in un commento che «la cancellazione arriva
a fine finestra operativa», ma nessuno cancellava niente: il token era quello
dello spegnimento del servizio o del Ctrl-C. Su volumi reali quelle fasi
proseguivano nella mattina lavorativa, in concorrenza con l'operativo — cioè
esattamente ciò che il pacing fra le slice, `DEADLOCK_PRIORITY LOW` e le
transazioni corte esistono per evitare.

**Decisione.** `PurgeWindowGuard` produce un token legato a quello del
chiamante e che scatta alla chiusura della finestra più una tolleranza
(`WindowGrace`, cinque minuti di default). I due punti d'ingresso lo aprono e
lo passano all'orchestratore al posto del token di spegnimento.

**Perché non riusare il controllo del coordinatore.** Il coordinatore chiude la
notte in modo ordinato: verifica la finestra fra una slice e l'altra e
restituisce `WindowClosed`, senza eccezioni. Quel meccanismo resta il percorso
normale e non è stato toccato. Il token è la rete per il caso che il
coordinatore non copre, cioè una fase che non ha punti di verifica interni.

**Perché la tolleranza.** Senza, il token scatterebbe nello stesso istante in
cui il coordinatore decide di fermarsi, e la chiusura ordinata diventerebbe
un'eccezione. Con la tolleranza, la notte normale non vede mai il token: se
scatta, è perché una fase ha davvero superato la finestra. Per questo la
scadenza si registra come `LogError` e non come informazione — è un'anomalia da
misurare, non un evento previsto.

**Conseguenze.**

- L'orchestratore era già pronto: `OperationCanceledException` non cambia fase,
  non conta come interruzione e lascia il run riprendibile dal checkpoint.
- Uno sforamento non fa proseguire con le strategie successive: aprirne una
  nuova fuori finestra sarebbe la cosa che si sta evitando.
- `purge once` esce con codice **5** quando la finestra ha interrotto una
  strategia. È una scelta di policy, non tecnica: il run riprende da solo la
  notte dopo, ma uscire con zero renderebbe lo sforamento invisibile a UC4. Chi
  configura gli allarmi deve sapere che 5 significa «rimandato», non «rotto».
- `--no-window` continua a funzionare: disattiva `WindowEnabled`, e il
  guardiano restituisce un ambito senza scadenza.
- `PurgeHousekeeping` continua a usare il token di spegnimento: ha già il
  proprio controllo di finestra fra un lotto e l'altro.

---

## D-10 — Un collettivo anomalo viene escluso in selezione, non fallisce il run

**Contesto.** `SelectEligibleCollectives` verificava solo stato e data dei
componenti. Un componente referenziato da `Model`, o con uno storico di
dettaglio che punta a un dettaglio fuori dall'aggregato (C7), entrava nei
candidati e veniva intercettato in `Validating` da V2 o V1. `Validating` è
fail-hard: il run andava in `Failed`, terminale, e la notte dopo un run nuovo
falliva allo stesso punto. Un solo collettivo anomalo bloccava l'intera
strategia finché qualcuno non correggeva i dati a mano. L'appartenenza
ambigua faceva lo stesso per un'altra via: `throw` dal `SelectAsync`.

**Decisione.** La selezione collettiva procede in tre tempi: eleggibili,
esclusioni per motivo, componenti. Le esclusioni sono `UPDATE` idempotenti
che portano il collettivo da `Selected` a `Excluded` con un `ExcludedReason`
(`ComponentHasModel`, `AmbiguousMembership`, `CrossRef:<tabella>`), con lo
stesso schema già usato per `ExecutionDateNull`. I componenti di un collettivo
escluso non entrano in `RunCandidateOrder`.

**Perché non degradare Validating.** Era la forma ovvia — «scarta e prosegui»
— ed è sbagliata: `Validating` è la rete di sicurezza contro un difetto della
selezione, e una rete che scarta in silenzio non è più una rete. Dopo queste
`UPDATE` un finding in V1/V2 su un run collettivo è un bug, e deve fermare
tutto come prima. `ValidateOrderBelongsToSingleCollective` resta, ma cambia
significato: da controllo di dominio a post-condizione di
`ExcludeAmbiguousCollectives`.

**Conseguenze.**

- Le esclusioni per C7 sono generate da `PurgeTopology.DetailHistoryTables`:
  una tabella di storico aggiunta alla topologia produce da sola la propria
  esclusione. `CollectiveExclusionContractTests` verifica che i motivi
  stiano nei 60 caratteri della colonna.
- `ExcludeAmbiguousCollectives` esclude *entrambi* i membri della coppia.
  Funziona perché una singola `UPDATE` legge lo snapshot precedente allo
  statement. Non è riproducibile nello schema di prova, che ha un indice
  univoco su `CollectiveOrderGroupOrder.OrderId`; se anche quello reale ce
  l'ha, l'esclusione non scatterà mai ed è solo difesa.
- `CountCollectiveAggregate` filtra ora `State = 'Selected'`. Non è
  ridondante: i collettivi `Excluded` hanno `BatchNo NULL` e nessuna `DELETE`
  li tocca, ma il conteggio previsionale li includeva già prima per
  `ExecutionDateNull`, e `vDryRunVsActual` riportava uno scostamento
  inventato.
- Il report del dry-run elenca i collettivi esclusi per motivo. Chi approva
  deve vederlo quanto i conteggi: sono aggregati che restano a database
  finché qualcuno li guarda.
- Punto aperto per il business: un componente con `StandingOrder = 1` dentro
  un collettivo. `TerminatedStrategy` esclude i piani ricorrenti perché la
  loro soglia è `LastExecutionDate`; `SelectEligibleCollectives` no. Non è
  stato aggiunto come esclusione perché non è chiaro che il caso esista nel
  dominio, e un'esclusione al buio nasconderebbe la domanda invece di porla.

---

## D-11 — Una slice che fallisce per un errore di dati si divide, non si abbandona

**Contesto.** `BatchExecutionCoordinator` abbandonava l'intera slice a ogni
`Fatal`. Il caso tipico è un solo ordine sporco: una riga di storico scritta
fra `Expanding` ed `Executing`, quindi fuori dal set congelato. La `DELETE`
del gruppo 2 non la tocca, la `DELETE` su `Order` fallisce con 547, e fino a
`MaxOrdersPerBatch` ordini restavano a database in `Failed` per una riga sola.
Nessun run successivo li ripesca senza che qualcuno li guardi.

**Decisione.** Se l'esito è `Fatal` e `Splittable`, il coordinatore chiede a
`IBatchWorkProvider.SplitAsync` di dividere la slice in due figlie per
aggregato, che entrano in coda come `Pending` con `BatchNo` nuovi. La madre
resta come traccia con `Status = 'Split'` e le figlie portano
`ParentBatchNo` e `SplitDepth`. Le metà buone committano; quella cattiva si
divide ancora, finché la slice che fallisce contiene un aggregato solo. A quel
punto `SplitAsync` restituisce zero senza toccare niente, e il coordinatore
abbandona: l'abbandono è circoscritto al colpevole.

**Perché la bisezione e non i singleton.** Ripianificare in N slice da uno
è la forma ovvia e costa N transazioni. La bisezione isola un colpevole in
circa 2·log₂N — diciotto per una slice da cinquecento — perché le metà buone
non vengono più toccate. Con molti colpevoli degrada verso i singleton, che è
comunque il comportamento corretto.

**Perché solo per gli errori di dati.** `SqlErrors.IsDataIntegrity` (547,
2627, 2601) è una lista chiusa, disgiunta da `IsTransient`. Un difetto del
programma — colonna mancante, permesso negato — fallirebbe ogni figlia, e
dividerlo produrrebbe 2·N transazioni fallite per scoprire una cosa sola.
`MaxSplitDepth` è il freno per il caso in cui un difetto si spacci per errore
di dati; con 2¹⁰ > `MaxOrdersPerBatch` massimo, il default arriva sempre al
singolo aggregato. Zero ripristina l'abbandono in blocco.

**Conseguenze.**

- L'unità indivisibile è l'aggregato: `COALESCE(CollectiveOrderId, OrderId)`.
  Un collettivo da tre ordini non si divide, e se è lui il colpevole viene
  abbandonato intero. È il contratto di `ValidateCollectiveBatchIntegrity`, e
  `SliceSplitTests` lo prova sul database.
- Le figlie vanno in coda, non davanti: si finisce il lavoro certo prima di
  tornare sul dubbio. Una finestra che chiude dopo una split trova le figlie
  la notte successiva, perché la split è una transazione sola.
- Il coordinatore non sa quanti aggregati contiene una slice — `OrderCount`
  non basta, un collettivo ne ha molti — e chiede allo store. È lo store a
  rispondere zero. `BatchExecutionCoordinatorContractTests` continua a
  valere: il coordinatore conosce `IBatchWorkProvider`, non `PurgeRunStore`.
- `AbandonedTotal`, l'housekeeping e `vDryRunVsActual` non cambiano:
  contano `Abandoned`, e `Split` non lo è. L'audit delle figlie porta i loro
  `BatchNo`; l'aggregazione per run somma comunque.
- La domanda «quale aggregato ha ucciso la slice 37» ha ora una risposta:
  `WHERE ParentBatchNo = 37 AND Status = 'Abandoned'`, e la slice trovata
  contiene un aggregato solo.
- `purge.slices_split` è una metrica nuova. Una crescita regolare dice che i
  dati cambiano fra selezione ed esecuzione più spesso di quanto il disegno
  assuma, ed è quello il problema da guardare, non la bisezione.
- Gli storici orfani si dividono per singolo storico, con lo stesso
  statement: la scelta del ramo la fa `IF EXISTS` su `RunCandidateOrder`,
  così lo store non deve conoscere la strategia.

---

## D-12 — Il cambio di stato in corsa non si riprova: si divide

**Contesto.** Quando la `DELETE` su `Order` cancellava meno righe di
`slice.OrderCount`, `SliceExecutor` restituiva `Retryable`. Il coordinatore
riprovava la stessa slice tre volte a distanza di `RetryDelay`, poi la
abbandonava in blocco: fino a `MaxOrdersPerBatch` ordini lasciati a database
perché uno solo era tornato in lavorazione.

**Decisione.** L'esito diventa `Fatal` con `Splittable = true`, cioè la
stessa strada della FK violata (D-11): la slice si divide finché l'ordine
cambiato resta solo, e viene abbandonato lui.

**Perché il retry era inutile.** Il conteggio della `DELETE` riflette lo stato
committato. Sotto `READ COMMITTED` la `DELETE` attende i lock di una
transazione concorrente e valuta il predicato dopo il commit; con RCSI attivo
fa lo stesso, perché le DML rileggono la riga al momento della modifica. Se
l'ordine non corrisponde più allo stato terminale, è perché qualcuno l'ha
cambiato *e committato*. Un ordine che torna in lavorazione non torna
`Executed` in cinque secondi; tre tentativi confermavano tre volte la stessa
cosa, e poi si perdeva tutta la slice.

**Cosa resta riprovabile.** Solo la contesa: deadlock, lock timeout, timeout di
comando (`SqlErrors.IsConcurrency`) e `CollectiveAtomicityViolation`, che è
un'incoerenza dello staging fra planning ed esecuzione, non dei dati. Sono i
casi in cui la *stessa* slice, riprovata *uguale*, può passare.

**Conseguenze.**

- L'ordine abbandonato ha `LastError = StatusChangedDuringExecution` sulla
  propria slice singleton, e nessuna slice del run consuma `AttemptCount` per
  questo motivo. `SliceSplitTests` lo verifica sul database.
- L'evento di log `PurgeSliceRetried` non copre più questo caso; il nuovo è
  `PurgeSliceStatusChanged`. `PurgeSliceRetried` resta per deadlock e
  collettivi non atomici.
- Un ordine *già cancellato* da altri fra selezione ed esecuzione produce lo
  stesso rowcount inferiore e segue la stessa strada. È corretto: il candidato
  finisce `Failed`, e la traccia dice perché.

---

## D-13 — Lo schema Purge si allinea con DbUp, non con le migration di EF Core

**Contesto.** `000_install_purge.sql` è derivato dalle migrazioni 005..012
una volta (vedi il commit di pulizia), e ciò che mancava non era un modello
ma il *tracciamento*: nessuno sapeva quali script fossero già passati su un
database. Le migration EF Core risolvono il tracciamento, e la domanda è
stata posta.

**Decisione.** `purge migrate` applica gli script di `db/` — gli stessi
file, incorporati nell'assembly — in ordine, uno per transazione,
registrandoli in `Purge.SchemaVersions`. Lo fa DbUp. `000` resta lo script
autonomo per chi installa con `sqlcmd`; `002` resta manuale.

**Perché non EF.** Il vincolo del README — nessun `DbContext` — riguarda il
contesto dell'*applicazione*, ferma a EF Core 3.1; un secondo contesto nel
progetto Purge sarebbe possibile. Ma tre cose lo rendono la scelta sbagliata
qui:

1. Di ciò che c'è negli script, il model builder descriverebbe solo le nove
   tabelle. La vista, nove indici filtrati, le `ALTER` idempotenti e le
   verifiche finirebbero in `migrationBuilder.Sql("...")`: SQL raw dentro
   C#, meno leggibile di com'è ora, e non più eseguibile dal DBA così com'è.
2. `002_indexes.sql` tocca tabelle dell'applicazione, di proprietà delle
   migration EF 3.1 dell'app. Un secondo contesto sullo stesso database
   condivide `__EFMigrationsHistory` — o la si rinomina, e da quel momento
   ogni migration dell'app vede indici che il suo modello non conosce.
3. Il DBA rivede SQL, non C#. Con EF lo script è un derivato
   (`migrations script --idempotent`); qui è la fonte.

**Conseguenze.**

- L'elenco degli script incorporati è nel `.csproj`, a mano.
  `MigrationContractTests` impone la regola: ogni file di `db/` è una
  migrazione salvo quelli esclusi con un motivo scritto nel test.
- Un database installato con `000` ha già tutto e nessun journal. Il primo
  `migrate` riesegue 001..0NN — idempotenti — e popola il journal. Non serve
  un baseline manuale, e questa è la ragione per cui gli script devono
  restare idempotenti.
- La fixture dei test usa `SchemaMigrator` al posto dell'elenco di script:
  ogni test di integrazione attraversa le migrazioni, e uno script
  dimenticato nel `.csproj` fallisce lì, non in produzione.
- `SchemaVerifier` resta invariato: dice se il database è allineato, non
  come allinearlo. I due sono complementari, e il primo continua a girare
  all'avvio di ogni esecuzione.
- `migrate` è un comando separato da `once`: il purge notturno non modifica
  lo schema, e chi allinea lo schema ha permessi DDL che l'utenza del purge
  non ha. `--status` elenca senza applicare ed esce con 5 se manca qualcosa,
  così uno scheduler può usarlo come controllo.
- La guardia sul nome del database (`PaymentOrder`) è la stessa di `000`.
  La fixture la disattiva per nome, non per caso.

---

## D-14 — Dapper dentro il livello dati, con la mappa `datetime2` come prima cosa

**Contesto.** `SqlExecutor` e `SqlSession` costruivano i comandi a mano e
leggevano per **posizione**: `r.GetInt32(5)`. Aggiungere `SplitDepth` a
`NextPendingSlice` (D-11) ha richiesto di toccare la query *e* l'indice nello
store; invertire due colonne sarebbe stato un bug silenzioso.

**Decisione.** Dapper vive dentro `SqlExecutor` e `SqlSession`. `ISqlExecutor`
e `IPurgeSession` non cambiano: i fake dei test restano identici, e
`SliceExecutor` non sa che esiste. Le letture di produzione passano a
`QueryAsync<T>`, mappato per nome su un `record` i cui parametri portano il
nome delle colonne.

**La mappa `DateTime → DbType.DateTime2` viene prima di tutto il resto.**
Dapper, come `AddWithValue`, manda un `DateTime` come `SqlDbType.DateTime`,
il cui minimo è il 1753. La filigrana della selezione paginata parte da
`DateTime.MinValue`: senza la mappa, il valore verrebbe rifiutato dal client
prima ancora di raggiungere il server, e la selezione si fermerebbe alla
prima pagina. Era la ragione d'essere di `SqlParam.Typed`, che resta
necessario per un'altra: un parametro **NULL** non ha un tipo deducibile dal
valore.

**Conseguenze.**

- Le query di produzione devono dare un **alias** a ogni colonna calcolata:
  `COUNT_BIG(*)` senza alias non ha un nome su cui mappare. Il rischio si
  sposta da "colonna sbagliata in silenzio" a "proprietà a zero", che è
  peggio se non ci si pensa — per questo `CountExcludedCollectivesByReason`
  ha guadagnato `Collectives = COUNT_BIG(*)`, e per questo i record di riga
  sono `private` accanto al metodo che li usa, dove si vedono insieme alla
  query.
- La conversione degli enum resta esplicita in `PurgeRunStore`: arrivano
  come stringa e vengono convertiti in `ToDomain()`, così un valore
  sconosciuto fallisce dicendo quale run lo contiene.
- `QueryAsync(map)` posizionale resta su `ISqlExecutor` per i test e le
  letture ad hoc, dove la query e la lambda si leggono a due righe di
  distanza.
- `SqlBulkCopy` in `BatchPlanner`, `DEADLOCK_PRIORITY LOW`, la transazione
  esplicita, `READ COMMITTED` e i batch multi-statement non cambiano: Dapper
  esegue ciò che gli si dà, e la politica del purge resta dove era.
- `ScalarAsync<T>` delega a `ExecuteScalarAsync<T>`, che conserva il
  comportamento precedente: NULL dal server diventa `default(T)`.


---

## D-15 — La deriva dello schema si rileva all'avvio, non di notte

**Contesto.** `RetentionSql` nomina esplicitamente una dozzina di colonne
applicative e nessuna tabella con `SELECT *`, quindi una colonna aggiunta a
`Order` o a una sua figlia non ha alcun effetto sul purge. Quattro
cambiamenti però contano, e due passavano senza difese automatiche:

1. **una tabella nuova che referenzia `Order` o `OrderHistory`** — non è in
   `PurgeTopology`, nessuna `DELETE` la tocca, la FK fa fallire la
   cancellazione della testata con errore 547. Dopo D-11 il sintomo è una
   sequenza di aggregati abbandonati, uno per uno, ogni notte;
2. **uno stato terminale nuovo** — `TerminalStates` è una costante: quegli
   ordini non diventano mai eleggibili, e *non succede niente*. È l'unica
   deriva silenziosa;
3. un tipo di pagamento nuovo — variante del caso 1;
4. una colonna rinominata o rimossa fra quelle usate — errore 207 immediato,
   visibile alla prima esecuzione anche in dry-run.

Il caso 1 era coperto da `004_verify_fk.sql`, che però è manuale e porta due
liste di nomi copiate a mano, quindi soggette a divergere dalla topologia.
Il caso 2 non era coperto da nulla.

**Decisione.**

- `SchemaVerifier` confronta le foreign key reali di `Order` e `OrderHistory`
  con i figli attesi, derivati da `PurgeTopology`. Una tabella inattesa
  impedisce l'avvio, con il suo nome nel messaggio.
- Il report del dry-run censisce gli stati non riconosciuti come terminali,
  con quanti ordini oltre soglia li portano e da quando.

**Perché un censimento e non un controllo, per gli stati.** Se uno stato
nuovo sia conclusivo lo sa il dominio, non il purge: bloccare l'esecuzione
perché è comparso `Settled` sarebbe sbagliato quanto ignorarlo. Il report lo
mostra a chi lo legge — che è la stessa persona che può rispondere. Il
conteggio è limitato agli ordini oltre soglia perché è quello che rende la
domanda urgente: due milioni di righe vecchie di sei anni sono un problema,
dieci righe di ieri no. Gli stati degli abbandoni (`Created`,
`PartiallyAuthorised`) sono esclusi: sono la popolazione di una strategia
esistente e segnalarli a ogni run sarebbe rumore, ed è il rumore che rende
inutili i censimenti.

**Perché solo i due genitori dell'aggregato.** Una tabella nuova che
referenzia un dettaglio — poniamo `BankTransfer` — non viene rilevata. È un
limite dichiarato e verificato da un test: sarebbe un grafo diverso da quello
che la topologia descrive, e va affrontato lì, non allargando un controllo
che a quel punto segnalerebbe anche ciò che non c'entra.

**Conseguenze.**

- Le liste in `004_verify_fk.sql` restano per chi esegue le verifiche senza
  avviare il motore, ma non sono più l'unica difesa: se divergono dalla
  topologia, è quella copia a essere vecchia. Lo script lo dice.
- `PurgeTopology.ExpectedChildren()` deriva dai gruppi di cancellazione:
  una tabella aggiunta alla topologia compare da sola nel controllo, e non
  esiste una seconda lista da ricordarsi di aggiornare.
- Un ambiente in cui l'applicazione ha già aggiunto una tabella legata a
  `Order` non parte più finché la topologia non viene aggiornata. È voluto:
  prima partiva e cancellava tutto il resto lasciando quegli aggregati a
  database senza che nessuno decidesse.

---

## D-16 — La finestra vive in un fuso dichiarato, e la sua scadenza è un istante

**Contesto.** `PurgeWindowGuard` leggeva `TimeProvider.GetLocalNow()`: la
finestra era l'ora locale dell'host, e nessuno dichiarava quale dovesse
essere. In un container `TZ` non è impostata e il fuso locale è UTC, quindi
"01:00–05:00" diventa 02:00–06:00 italiane d'inverno e 03:00–07:00 d'estate.
Il purge girerebbe nell'ora sbagliata **tutte le notti**, senza errori e senza
avvisi.

Separatamente, `TimeUntilWindowEnd` sottraeva due `TimeOnly` — cioè misurava
orologio — mentre il `CancellationTokenSource` che ne derivava misura tempo
reale. Le due cose divergono di un'ora nelle notti di cambio ora.

**Decisione.** Un `TimeZoneId` esplicito in configurazione, con il fuso
dell'host solo come ripiego e **scritto nel log all'avvio**; e la scadenza
calcolata costruendo l'istante di chiusura in quel fuso, non per differenza di
orari.

**Che cosa era rotto e cosa no.** Vale la pena registrarlo, perché il difetto
era più circoscritto di quanto sembrasse: `IsWithinWindow` confronta ore di
orologio ed era **corretto**, quindi il coordinatore fra una slice e l'altra si
è sempre fermato all'ora giusta, anche nelle notti di cambio. A sbagliare era
solo il token che governa le fasi lunghe — selezione, espansione — e in una
sola direzione dannosa: nella notte di primavera la scadenza risultava un'ora
più generosa, e una fase poteva proseguire fino alle 06:00. Nella notte
d'autunno chiudeva un'ora prima, che costa lavoro non fatto e nient'altro.

**Conseguenze.**

- Un identificativo di fuso sconosciuto solleva all'avvio, che è il momento
  giusto per accorgersene.
- I due casi patologici del cambio ora sono gestiti esplicitamente: se
  l'orario di chiusura non esiste (salto di primavera) si prende il primo
  istante valido; se esiste due volte (ritorno all'ora solare) si prende il
  primo, perché chiudere in anticipo costa lavoro e chiudere in ritardo porta
  il purge nell'operatività.
- Tutti i punti che leggevano l'orologio passano da `PurgeOptions.Now(clock)`,
  che converte da UTC: nessuno dipende più dal fuso dell'host.

---

## D-17 — Un tetto al peso del singolo aggregato

**Contesto.** `MaxRowsPerBatch` limita la slice, ma un aggregato che lo supera
da solo diventa una slice dedicata **senza limite superiore**. Un collettivo
da duecento componenti con molte revisioni può pesare centomila righe: una
transazione che innesca la lock escalation e gonfia il log, cioè esattamente
ciò che il packing esiste per evitare. Il commento nel codice diceva "si
accetta consapevolmente un picco di lock", che è vero fino a un certo peso e
falso oltre.

**Decisione.** `MaxAggregateWeight` (default dieci volte `MaxRowsPerBatch`,
zero disattiva): sopra quella soglia l'aggregato non viene assegnato a nessuna
slice, passa a `Excluded` con motivo `AggregateTooLarge` e compare nel report.
Resta a database finché qualcuno decide come trattarlo.

**Perché escludere e non spezzare.** Spezzare un collettivo è escluso da D-3.
Spezzare un ordine singolo con migliaia di revisioni sarebbe possibile — gli
storici si potrebbero cancellare a lotti prima della testata — ma è un
meccanismo nuovo, con una sua atomicità da dimostrare, per un caso che
dovrebbe essere raro. Escludere e censire costa niente e rende il caso
visibile; se il censimento mostrasse che è frequente, allora varrebbe la pena
costruire lo spezzamento.

**Conseguenze.**

- Un tetto minore di `MaxRowsPerBatch` è rifiutato dal costruttore: renderebbe
  indecidibile il caso intermedio, in cui un aggregato dovrebbe essere insieme
  slice dedicata ed escluso.
- `ApplyAssignments` ripete il filtro sugli esclusi in tutte e tre le `UPDATE`:
  senza, uno storico o una testata collettiva finirebbero in una slice che non
  contiene il loro ordine.
- Il default va tarato sul p99 reale dopo il primo dry-run. Un valore troppo
  basso trasforma in esclusioni aggregati che il motore reggerebbe.

---

## D-20 — Il fuso è obbligatorio per le esecuzioni reali

**Contesto.** D-16 ha introdotto `TimeZoneId` e la documentazione lo dichiara
«da valorizzare in produzione», ma il codice non lo pretendeva: senza, si
ripiega sul fuso della macchina. Una revisione esterna ha osservato che così
la garanzia si affida alla disciplina operativa, ed è più debole di un
controllo.

Ha ragione, e il rischio non è teorico: su un host in UTC — la norma in un
container — «01:00–05:00» diventa 02:00–06:00 italiane d'inverno, e il purge
lavora un'ora dentro l'operatività tutte le notti, senza errori.

**Decisione.** Un'esecuzione in modalità `--delete` con la finestra attiva e
nessun fuso dichiarato viene rifiutata all'avvio, con codice 2 e un messaggio
che nomina il fuso della macchina che sarebbe stato usato.

**Perché solo per la cancellazione reale.** La simulazione resta permissiva:
serve a produrre il report da approvare, non tocca nulla, e pretendere il fuso
lì bloccherebbe il percorso che porta all'approvazione. Un dry-run nell'ora
sbagliata produce lo stesso report.

**Perché prima del gate.** Rifiutare per configurazione mancante è un motivo
diverso dal rifiutare per policy non approvata, e confonderli nel messaggio
manderebbe chi legge a cercare un'approvazione che non serve.

**Conseguenza.** `--no-window` resta la via d'uscita per chi ha davvero
bisogno di eseguire senza limite orario: è già esplicita e registrata nel log,
e chi la usa sta dichiarando di sapere cosa fa.

---

## D-19 — Il tetto per aggregato fa parte della policy approvata

**Contesto.** D-16 e D-17 avevano classificato `MaxAggregateWeight` fra i
parametri operativi, fuori dall'impronta della policy, per analogia con
`MaxRowsPerBatch`. Era sbagliato, e una revisione esterna lo ha individuato.

`MaxRowsPerBatch` cambia in quante transazioni si divide il lavoro:
l'insieme cancellato resta identico. `MaxAggregateWeight` decide se un
aggregato viene cancellato o resta a database. È perimetro, non prestazione.

**Il caso concreto.** Si approva una simulazione con il tetto a 30 000, e il
report mostra un collettivo da 80 000 fra gli aggregati esclusi: chi approva
sta approvando anche quell'esclusione. Alzando poi il tetto a 100 000 senza
che l'impronta cambi, quell'aggregato diventerebbe cancellabile con
un'approvazione che nessuno ha dato per lui — esattamente ciò che il gate
esiste per impedire.

**Decisione.** `MaxAggregateWeight` entra nell'impronta e nella descrizione
leggibile della policy. Cambiarlo, in qualunque direzione, richiede una nuova
approvazione.

**Perché anche nella direzione che cancella meno.** Abbassare il tetto
esclude più aggregati, quindi cancella meno: non è pericoloso. Ma il gate non
approva un livello di rischio, approva un perimetro, e un perimetro diverso
va esaminato — se non altro perché chi legge il rapporto di chiusura deve
poter sapere quale tetto era in vigore.

**Conseguenza operativa.** Le approvazioni già registrate decadono: l'impronta
cambia per tutte. Va rifatto un dry-run e una nuova approvazione prima della
prossima esecuzione reale. Su un sistema non ancora in produzione è gratuito;
dopo lo sarebbe stato molto meno, ed è la ragione per cui questa correzione
non poteva aspettare.

**Il criterio, per le prossime volte.** Un parametro entra nell'impronta se
esiste un dato che, al variare del parametro, passa da cancellato a non
cancellato o viceversa. Non conta quanto sia importante, né quanto sia
probabile che qualcuno lo cambi.

**Due prove, non una.** `ExecutionModeTests` verifica l'aritmetica —
l'impronta cambia — e `ApprovalGateTests` verifica il percorso reale: con il
tetto alzato il gate rifiuta l'esecuzione. La seconda è quella che conta,
perché il gate potrebbe smettere di consultare l'impronta senza che
l'aritmetica se ne accorga. Tornando al valore approvato l'approvazione
riprende a valere: si approva una policy, non un momento nel tempo.

---

## D-18 — Le decisioni collegate al runtime hanno un test che lo dice

**Contesto.** Una revisione esterna ha sostenuto — sulla base di una copia
non aggiornata del repository — che il gate di approvazione, la modalità da
riga di comando e il tetto per aggregato fossero implementati ma non
invocati. L'affermazione era falsa, e verificarla ha richiesto di aprire tre
file.

Il punto però non è chi avesse ragione: **non era falsificabile**. Nessun
test diceva che quei collegamenti esistessero, e nessuno si sarebbe accorto
se qualcuno li avesse rimossi. Un componente di sicurezza scollegato è
indistinguibile da uno assente, e scollegarlo è un'operazione di una riga:
togliere una chiamata, cambiare un default, dimenticare un parametro.

**Decisione.** `DecisionWiringTests` verifica il collegamento delle decisioni
che, se scollegate, fallirebbero in silenzio: il gate interrogato prima
dell'esecuzione, la modalità presa dalla riga di comando e non dalla
configurazione, il tetto per aggregato che arriva fino al packer, il fuso
usato dalla pianificazione, l'assenza di letture dirette dell'orologio
dell'host, la finestra disattivabile solo su richiesta esplicita.

**Perché controlli sul testo del sorgente.** Sono grezzi, e lo dichiarano.
L'alternativa — avviare l'host in un test e osservarne il comportamento —
costerebbe molto di più e verificherebbe la stessa cosa. Questi test non
provano che il sistema funzioni, provano che un collegamento non sia stato
tolto; per il resto ci sono i test di integrazione. Un falso allarme si
risolve in trenta secondi, un collegamento perso in silenzio no.

**Nella stessa revisione** è emerso un difetto reale: `RetentionCronService`
interpretava l'espressione cron con `TimeZoneInfo.Local` mentre la finestra
usava il fuso dichiarato (D-16). Su un host in UTC il servizio si sarebbe
svegliato due ore dopo l'apertura della finestra, perdendo due ore di lavoro
ogni notte senza alcun errore. Convertendo le letture dell'orologio avevo
mancato quella riga: la pianificazione non legge l'ora, la interpreta.

**Conseguenza sul documento.** `BatchPacker` era descritto come «funzione
pura» mentre il codice dichiara di essere un accumulatore con stato. La
formulazione corretta è *deterministico e privo di I/O*: ciò che conta
architetturalmente è che non tocchi il database, non che sia privo di stato —
lo stato serve a tenere insieme i componenti di un collettivo che arrivano su
chiamate successive.
