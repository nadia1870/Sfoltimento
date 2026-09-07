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
