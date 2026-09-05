# V5 — su base V4.2 (Solution A)

La V4.2 è stata presa come base: il refactor delle strategie in essa contenuto
è corretto e non è stato rifatto. Nessun riferimento pendente, nessun `using`
mancante, e tutte le scritture su `Purge.*` preservate — incluso il censimento
dei collettivi privi di `ExecutionDate`, che era la cosa più facile da perdere.

## 1. Reinserito `SchemaVerifier` (regressione)

La V4.2 è partita da uno snapshot precedente alla sua introduzione. È il
componente che confronta `PurgeTopology` con `sys.tables` e blocca l'avvio
elencando **tutte** le tabelle mancanti.

Senza, una tabella assente emerge come errore 208 a metà della fase di
validazione — dopo che il run è stato creato e la selezione eseguita — e ne
nomina una sola, così la diagnosi procede per tentativi. È esattamente
l'errore incontrato su `PaymentOrder.IpQRBillHistory`.

Invocato sia dal cronjob all'avvio sia dalla modalità `once`, prima che venga
creato qualsiasi run. Codice di uscita `2`, distinto da `1` (run fallito).

## 2. `EffectiveCutoff` spostato nella strategia

Era l'ultimo `switch` sull'enum rimasto fuori dal refactor, su `PurgeRun`.
Quale delle due soglie congelate si applica è una decisione della strategia, non
una proprietà del modello del run.

Sostituito da `IPurgeStrategy.CutoffOf(PurgeRun)`, con default
`run.RetentionCutoff` e override in `AbandonedStrategy`.

## 3. Log di selezione uniformato

`CollectiveStrategy` emetteva un formato proprio; le altre quattro emettevano
`PurgeSelectionCompleted`. Un'asimmetria del genere rompe le query sui log e gli
alert costruiti su quel nome. Ora emette entrambe le righe: il dettaglio sui
collettivi e l'evento comune.

## Non toccato

Il refactor delle strategie della V4.2, `RetentionPolicy`, il resolver, le cinque
implementazioni.

## Rimane aperto

`RetentionPolicy` estrae correttamente il calcolo delle soglie, ma `PurgeOptions`
mantiene due metodi wrapper e `PurgeRunStore` chiama quelli. Sono due strade per
lo stesso calcolo: prima o poi qualcuno ne modificherà una sola. Non l'ho
cambiato perché tocca la creazione del run, che è l'unica cosa che ha già girato
correttamente contro il database.

## Non verificato

Nessuna compilazione: l'ambiente non ha accesso a NuGet. I controlli statici
eseguiti — `using` di SqlClient, tipi risolvibili, bilanciamento dei blocchi —
non sostituiscono `dotnet build`.

## V5.x — Phase Pipeline Refactoring

- Estratto il workflow di `RetentionOrchestrator` in una pipeline di `IPurgePhase`.
- Ogni fase esegue il proprio lavoro e restituisce una `PhaseResult`; la transizione persistita e' centralizzata nell'orchestratore.
- Eliminata la sequenza di `if (run.Phase == ...)` duplicata nell'orchestratore.
- `Created` e `Selecting` sono gestiti dalla stessa `SelectingPhase` per compatibilita' con i run esistenti.
- Introdotta la fase persistita `CollectiveTail`, consentendo la ripresa esplicita del cleanup finale dei collettivi.
- Il dry-run resta una modalita' della `PlanningPhase`, non uno stato della macchina a stati.
- In caso di cancellazione non viene piu' forzato lo stato `Executing`: il run resta nella fase realmente in corso e viene ripreso da quel checkpoint.


## V5.1 — Collective atomic execution

- Aggiunto `CollectiveOrderId` a `Purge.RunCandidateOrder` per materializzare il legame tra ordine e Collective.
- Aggiunto `BatchNo` a `Purge.RunCandidateCollective`.
- `BatchPlanner` pianifica il Collective come unita' indivisibile: tutti i suoi ordini ricevono lo stesso `BatchNo`.
- Un Collective oversized non viene suddiviso: resta una singola transazione.
- `CollectiveStrategy` include la cancellazione delle tabelle Collective nella stessa slice degli ordini.
- Aggiunta guardia transazionale `ValidateCollectiveBatchIntegrity` prima dei DELETE.
- `CollectiveTail` non effettua piu' delete per i nuovi run; resta solo come compatibilita' per run legacy e rifiuta l'esecuzione non atomica.
- Aggiunta migrazione `006_collective_atomicity.sql`.
