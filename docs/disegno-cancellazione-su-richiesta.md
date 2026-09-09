# Nota di disegno — Cancellazione su richiesta

**Stato:** proposta. Nessuna riga di codice scritta.
**Baseline:** `main` con D-15. Il motore di retention descritto in
`docs/architettura/` è il presupposto di tutto ciò che segue.

Questa nota copre due bersagli — il numero di relazione del debitore e il
`DynacosPaymentId` — che arrivano dallo stesso canale e condividono lo stesso
motore, ma hanno profili di rischio diversi.

Il capitolo 8 è il censimento dei punti aperti: è la parte da portare al
business e a Legal. Ogni punto ha le opzioni possibili e un comportamento
proposto in attesa di risposta, così l'implementazione può iniziare sui punti
non bloccanti senza pregiudicare le decisioni.

---

## 1. Che cosa si chiede

Cancellare ordini identificati da un criterio che non è l'anzianità:

- **per numero di relazione del debitore**, per chiusura del rapporto, dato
  inserito per errore o richiesta dell'autorità;
- **per `DynacosPaymentId`**, su richiesta della contabilità.

Entrambe le richieste arrivano come comando su una coda RabbitMQ.

## 2. Casi d'uso

### UC-1 — Chiusura del rapporto

| | |
|---|---|
| Attore | Sistema a monte (gestione rapporti), via coda |
| Bersaglio | `Order.DebtorAccountRelNr` |
| Cardinalità attesa | Da decine a migliaia di ordini |
| Stato degli ordini | Ci si attende siano tutti conclusi, ma non è garantito (PA-32) |
| Raggio d'azione | **Ampio.** Un numero sbagliato cancella i dati di un cliente vivo |
| Autorizzazione proposta | Approvazione umana obbligatoria, sempre |
| Esito atteso | Nessun ordine del cliente resta a database |
| Nodo principale | PA-30: la chiusura del rapporto non estingue l'obbligo di conservazione contabile |

### UC-2 — Dato inserito per errore

Meccanica identica a UC-1, ma la giustificazione giuridica è più solida: quei
dati non avrebbero dovuto esistere. La cardinalità è tipicamente bassa.
Distinguerlo da UC-1 nell'origine della richiesta serve a Legal e all'audit,
non al motore.

### UC-3 — Richiesta dell'autorità

Come UC-1, con un atto formale a monte. La distinzione conta per due ragioni:
l'atto ha un protocollo che diventa il riferimento esterno, e potrebbe imporre
una cancellazione anche dentro il periodo di conservazione, che negli altri
casi sarebbe discutibile.

### UC-4 — Cancellazione di un pagamento singolo (contabilità)

| | |
|---|---|
| Attore | Contabilità, via coda |
| Bersaglio | `Order.DynacosPaymentId` |
| Cardinalità attesa | Un ordine |
| Raggio d'azione | **Ristretto.** L'errore possibile è circoscritto |
| Autorizzazione proposta | Automatica, entro un tetto misurato (§6.3) |
| Esito atteso | Risposta sulla coda con l'esito |

### UC-5 — Cancellazione di un pagamento che è un gruppo collettivo

È UC-4, ma il `DynacosPaymentId` identifica un **gruppo** di un ordine
collettivo, e il collettivo può contenere più gruppi con `DynacosPaymentId`
diversi.

| | |
|---|---|
| Bersaglio effettivo | Un `CollectiveOrderGroup`, quindi N ordini componenti |
| Conflitto | La richiesta chiede una **cancellazione parziale del collettivo** |
| Raggio d'azione | Ristretto in apparenza, potenzialmente ampio: cancellare l'intero collettivo tocca gruppi che nessuno ha chiesto |
| Nodo principale | PA-40 |

**Perché è il caso più difficile.** `CollectiveOrder` porta `TotalAmount` e
`TransactionCount`: sono i totali contabili della disposizione. Cancellare un
gruppo su tre lascia una testata i cui totali non corrispondono più al
contenuto, e **riscriverli sarebbe falsificare un documento contabile**. È lo
stesso ragionamento di D-3, con una differenza: qui il caso non è ipotetico,
è la struttura normale dei dati.

## 3. Che cosa si riusa e che cosa cambia

**Si riusa senza modifiche:** staging, espansione e pesatura, planning e
packing, slice in transazione per aggregato, topologia di cancellazione,
bisezione (D-11), audit transazionale (D-2), finestra operativa, ripresa da
checkpoint (D-5), verifica dello schema (D-15).

**Cambia alla radice:**

| | Retention | Su richiesta |
|---|---|---|
| Asse | Anzianità | Identità |
| Bersaglio | Una query su una soglia | Un elenco, congelato alla simulazione |
| Autorizzazione | Policy approvata una volta, valida per sempre | Per singola richiesta |
| Innesco | Pianificazione notturna | Messaggio su coda |
| Ripetibilità | Ogni notte, ed è voluto | **Esattamente una volta** |
| Stato ammesso | Solo terminale | Da decidere (PA-32) |
| Modelli | Esclusi (C5) | Cancellati |
| Obbligo di prova | "Ho cancellato ciò che era oltre soglia" | "Ho cancellato **tutto e solo** ciò che riguarda questo bersaglio" |

## 4. Perimetro

Un ordine entra nel perimetro se corrisponde al bersaglio della richiesta. Da
lì, per aggregato:

| Elemento | Trattamento | Nota |
|---|---|---|
| `Order`, `OrderHistory`, dettagli e storici di dettaglio | Cancellati | Identico alla retention |
| `StandingOrder` | Cancellato | Il piano segue l'ordine |
| `Model` | **Cancellato** | Nella retention è un motivo di esclusione (C5) |
| `Category` | Da definire | PA-33 |
| `CollectiveOrder` e coda | Per intero, o niente | §5 |

Fuori dal perimetro, oggi: gli ordini in cui la relazione bersaglio compare
come **beneficiario** (PA-31), e qualunque dato fuori dallo schema
`PaymentOrder`.

## 5. Atomicità: il nodo comune ai due bersagli

Il motore ha un'invariante non negoziabile: **un ordine collettivo si cancella
per intero o non si cancella**. Da qui tre situazioni.

**Per relazione.** Il business ha stabilito che un collettivo non può essere
condiviso fra debitori: se un componente è nel perimetro lo è tutto il
collettivo, e l'atomicità si applica identica. È però un'invariante del
dominio e non del database: va **verificata** con una regola nuova (V6) che
conta i collettivi con componenti di relazioni diverse, non assunta.

**Per `DynacosPaymentId` su ordini singoli.** Nessun problema.

**Per `DynacosPaymentId` su un gruppo collettivo (UC-5).** Quattro opzioni, la
scelta è del business (PA-40):

| Opzione | Effetto | Costo |
|---|---|---|
| **A. Estendere al collettivo intero** | Si cancellano anche i gruppi non richiesti | Si cancella più di quanto chiesto: altri `DynacosPaymentId` spariscono senza che la contabilità lo sappia. Richiede almeno una notifica |
| **B. Escludere e censire** | Il collettivo resta intero, la richiesta non è soddisfatta | La contabilità non ottiene ciò che chiede; serve un percorso manuale |
| **C. Cancellare solo il gruppo** | La richiesta è soddisfatta alla lettera | **Non praticabile**: lascia i totali della testata incoerenti, e correggerli sarebbe falsificazione |
| **D. Anonimizzare il gruppo** | I dati personali spariscono, i totali restano coerenti | È un motore diverso, non una variante di questo |

**Comportamento proposto in attesa di risposta: B.** È l'unico che non
cancella nulla di non richiesto e non falsifica nulla. La richiesta risulta
`PartiallyExecuted` e torna a un umano.

Una distinzione che vale per tutti i casi: **il run si conclude, la richiesta
no.** Una cancellazione che riporta successo mentre qualcosa è rimasto è il
peggior esito possibile, quindi lo stato della richiesta è separato da quello
del run.

## 6. Arrivo dalla coda

### 6.1 Il messaggio non cancella

Il consumatore fa una cosa sola: **persiste la richiesta e conferma il
messaggio**. La cancellazione è un passo separato che legge dalla tabella
delle richieste.

Tre ragioni indipendenti. RabbitMQ garantisce consegna *almeno una volta*, e
un messaggio riconsegnato durante la cancellazione sarebbe un disastro mentre
una richiesta già registrata è un no-op. Il broker non è un archivio, e la
prova dell'autorizzazione deve sopravvivergli. La finestra operativa non
permette comunque di cancellare al momento dell'arrivo (PA-47).

Corollario: se è il messaggio ad autorizzare, **il messaggio è il documento**
e va conservato integralmente — identificativo, mittente, marca temporale,
contenuto e sua impronta — non riassunto in tre colonne.

### 6.2 Esattamente una volta, non "almeno una volta"

«Cancella la relazione 4711» **non è idempotente nel tempo**: rieseguirlo fra
sei mesi cancella dati arrivati nel frattempo, che nessuno ha esaminato. Con
una coda il caso è realistico — riconsegna dopo un guasto, coda di lettere
morte rigiocata a mano, ambiente ripristinato da backup.

Due difese, entrambe necessarie:

- la richiesta è identificata dal message id ed è eseguibile **una sola
  volta**;
- il perimetro è congelato alla simulazione, con un'impronta ricalcolata
  prima dell'esecuzione: se è cambiato, l'esecuzione si ferma.

### 6.3 L'autorizzazione proporzionata al raggio d'azione

Il motore può **misurare** l'ampiezza prima di agire. Questo permette una
regola sola per entrambi i bersagli, applicata a numeri diversi:

| Ambito misurato in simulazione | Trattamento proposto |
|---|---|
| 0 ordini | Rifiuto: probabile identificativo errato. Risposta al richiedente, nessuna cancellazione |
| 1 … N (N basso, da concordare) | Esecuzione automatica, tracciata |
| Oltre N | In attesa di approvazione umana |
| Oltre una soglia alta | Rifiuto: qualcosa non torna |

Così UC-4 resta automatico come la contabilità si aspetta, UC-1 finisce sempre
nel ramo con approvazione, e un difetto che facesse selezionare diecimila
ordini a un `DynacosPaymentId` viene fermato dal tetto e non dalla fortuna. Le
soglie sono PA-48.

### 6.4 Che cosa il motore può e non può validare

Non può stabilire se una richiesta sia legittima: se chi ha le credenziali
chiede di cancellare la relazione 4711 con un riferimento plausibile, nessun
controllo tecnico dice se quella pratica esista. La fiducia sta nell'atto
autorizzativo, fuori dal database; il motore garantisce che sia **tracciato e
attribuibile**, non che sia vero.

Può però ridurre di molto il rischio vero, che non è il malintenzionato ma il
**refuso**:

- **far riconoscere il cliente, non il numero**: la simulazione mostra periodo
  coperto, numero e tipo di ordini, nomi dei modelli, importo aggregato —
  elementi con cui un umano dice «sì, è lui»;
- **digitare il numero una volta sola**: da lì in poi si lavora con
  l'identificativo della richiesta, e il comando di esecuzione non contiene il
  bersaglio;
- **ridigitazione da parte di chi approva**, nel ramo umano: un refuso non si
  ripete identico da due persone diverse;
- **segnali di implausibilità** che allertano senza decidere: zero ordini,
  ordini negli ultimi giorni, piani ricorrenti attivi, volume fuori scala. Se
  uno scatta, l'approvazione richiede una motivazione scritta;
- **verifica sull'anagrafica**, se esiste ed è raggiungibile (PA-49): per UC-1
  il motore può pretendere che la relazione esista e risulti chiusa. È l'unica
  validazione sostanziale possibile.

### 6.5 La richiesta come entità

`Purge.DeletionRequest`: identificativo, message id, tipo e valore del
bersaglio, origine, riferimento esterno, richiedente, marca temporale del
messaggio, contenuto originale e sua impronta, `DryRunRunId`, approvatore e
data, stato (`Received`, `Simulated`, `Approved`, `Executed`,
`PartiallyExecuted`, `Rejected`), esito.

## 7. Prova di completezza

È l'obbligo che distingue questi casi d'uso dalla retention: non «ho cancellato
ciò che era oltre soglia» ma «ho cancellato **tutto e solo** ciò che riguarda
questo bersaglio».

Il rapporto di chiusura contiene: righe cancellate per tabella; aggregati
esclusi con il motivo; **verifica positiva** che nessuna riga con quel
bersaglio sia rimasta nelle tabelle del perimetro — un *cerco e non trovo*,
eseguito dopo l'ultima slice; identità di chi ha chiesto e approvato;
riferimento esterno. Se le esclusioni non sono zero, la richiesta resta
`PartiallyExecuted`.

Per UC-4 e UC-5 lo stesso esito torna sulla coda come evento (PA-46): è la
prova di completezza in forma leggibile da una macchina.

## 8. Censimento dei punti aperti

### 8.1 Perimetro e base giuridica

| ID | Questione | Opzioni | Proposta in attesa | Blocca? |
|---|---|---|---|---|
| **PA-30** | La chiusura del rapporto non estingue l'obbligo di conservazione contabile. Si può cancellare un ordine di due anni fa? La risposta può differire fra UC-1, UC-2 e UC-3 | (a) solo oltre la soglia di retention; (b) tutto, con base giuridica documentata per origine; (c) tutto solo per UC-3 | Nessuna: il perimetro non è definibile senza risposta | **Sì** |
| PA-31 | La relazione bersaglio come **beneficiario** in ordini di altri debitori: rientra? | (a) no, perimetro = solo debitore; (b) sì, e va individuato dove il beneficiario è memorizzato | (a), dichiarato come limite nel rapporto di chiusura | No |
| PA-35 | Ordine la cui testata ha oggi la relazione X ma i cui storici portano la relazione Y | (a) perimetro sulla sola testata; (b) anche sugli storici; (c) sugli storici, con esclusione se la testata è di altri | (a), più censimento dei casi in simulazione | No |
| PA-33 | Le `Category` del cliente vanno cancellate? Possono essere condivise fra relazioni? | (a) cancellate; (b) cancellate solo se non condivise; (c) escluse | (b), con censimento delle condivise | No |

### 8.2 Stato degli ordini

| ID | Questione | Opzioni | Proposta in attesa | Blocca? |
|---|---|---|---|---|
| **PA-32** | Gli ordini in stato non terminale vanno cancellati? Chi garantisce che nulla sia in lavorazione? | (a) esclusi e censiti; (b) cancellati comunque; (c) cancellati previa conferma dell'applicazione a monte | (a): il default più prudente, rende visibile il problema senza toccare ciò che è in volo | **Sì** per UC-1 |

### 8.3 Atomicità e `DynacosPaymentId`

| ID | Questione | Opzioni | Proposta in attesa | Blocca? |
|---|---|---|---|---|
| **PA-40** | Un `DynacosPaymentId` identifica un gruppo di un collettivo che ne contiene altri: la richiesta chiede una cancellazione parziale | §5, opzioni A–D | **B**: escludere, censire, richiesta `PartiallyExecuted` | **Sì** per UC-5 |
| PA-41 | `DynacosPaymentId` è univoco su `Order`? Quanti ordini può identificare al massimo? | Determina il tetto della soglia automatica | Tetto prudenziale basso, da alzare con i dati reali | No |
| PA-42 | È nullable? Che cosa fare di un comando con il campo vuoto? | (a) rifiuto in validazione del messaggio; (b) trattato come nessun risultato | (a): mai passare un valore vuoto alla query | No |
| PA-43 | Se il pagamento è un gruppo, la contabilità sa che gli altri gruppi dello stesso collettivo potrebbero essere coinvolti? | Comunicazione, non tecnica | Il rapporto e l'evento di risposta lo dichiarano esplicitamente | No |

### 8.4 Canale e autorizzazione

| ID | Questione | Opzioni | Proposta in attesa | Blocca? |
|---|---|---|---|---|
| **PA-44** | Il messaggio **autorizza** la cancellazione o crea una richiesta che va ancora approvata? Può differire fra UC-1 e UC-4 | (a) autorizza per UC-4, non per UC-1; (b) mai; (c) sempre | (a), con la soglia di §6.3 come discriminante | **Sì** |
| **PA-45** | Chi pubblica sulla coda e come si autentica? Chi può scrivere su quella coda può cancellare dati di produzione | Infrastrutturale | Da concordare con chi gestisce il broker | **Sì** |
| PA-46 | Il richiedente si aspetta una risposta? Con quale contenuto? | (a) evento di esito sulla coda; (b) nessuna | (a): è anche la prova di completezza leggibile da una macchina | No |
| PA-47 | Esecuzione immediata o in finestra? | (a) UC-4 immediato, UC-1 in finestra; (b) tutto in finestra | (a) se la latenza attesa è di minuti; (b) è più semplice e va bene se sono ore | No |
| PA-48 | Soglie del raggio d'azione (§6.3): quale N per l'automatico, quale per il rifiuto | Numerica | Da fissare sui dati reali dopo il primo censimento | No |
| PA-34 | Chi approva deve essere diverso da chi richiede? | (a) sì, obbligatorio; (b) sì per UC-1, no per UC-4 | (a) per il ramo umano | No |
| PA-49 | Esiste un'anagrafica delle relazioni interrogabile dal database, con lo stato del rapporto? | Determina se §6.4 può avere una validazione sostanziale | Se esiste, verifica obbligatoria per UC-1 | No |

### 8.5 Conservazione delle prove

| ID | Questione | Opzioni | Proposta in attesa | Blocca? |
|---|---|---|---|---|
| PA-36 | Quanto si conservano richieste e rapporti di chiusura? Sono la prova dell'avvenuta cancellazione | Presumibilmente più a lungo dei dati cancellati | Nessuna scadenza finché non deciso | No |
| PA-50 | Il rapporto di anteprima contiene elementi identificativi che servono a riconoscere il cliente: conservarlo significa tenere una copia parziale di ciò che si è cancellato | (a) due artefatti distinti — anteprima ricca non conservata, chiusura con soli conteggi; (b) conservare tutto | (a) | No |

### 8.6 Riepilogo dei bloccanti

**PA-30** (base giuridica), **PA-32** (stati non terminali), **PA-40**
(collettivo parziale), **PA-44** (il messaggio autorizza?), **PA-45**
(autenticazione del canale). Gli altri si possono chiudere con il default
proposto e rivedere dopo.

## 9. Sintesi dell'intervento, a punti chiusi

- Migrazione: `Purge.DeletionRequest`, colonne di collegamento su
  `Purge.PurgeRun`.
- Consumatore della coda che persiste e conferma, senza cancellare.
- Due strategie, `ByDebtorRelationship` e `ByDynacosPayment`, che condividono
  espansione, planning ed esecuzione.
- Regola di validazione V6: collettivi con componenti di relazioni diverse.
- Misura del raggio d'azione e instradamento automatico/umano.
- Verifica dell'impronta del perimetro fra simulazione ed esecuzione.
- Rapporto di chiusura con verifica positiva, ed evento di risposta.
- `Model` passa da esclusione a cancellazione **solo per queste strategie**:
  la retention resta com'è.

Il lavoro sul motore è contenuto. Il costo vero è in §6 e §7 — autorizzazione
per richiesta e prova di completezza — che non esistono oggi e non si
ottengono riusando ciò che c'è.
