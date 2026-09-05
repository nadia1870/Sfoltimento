# V3 — correzioni alla V2

## Bloccanti

### 1. `PurgeExecutionLock` non compilava
`SqlConnection` usata senza `using Microsoft.Data.SqlClient;` — CS0246.

### 2. Il planning non era idempotente
La correzione della macchina a stati introdotta in V2 è giusta, ma espone un
problema che prima restava latente: se il processo cade durante `Planning`, alla
ripresa `PlanAsync` viene rieseguito e `InitializeBatchProgress` reinserisce le
stesse `(RunId, BatchNo)`, violando la chiave primaria e portando il run in
`Failed`.

Aggiunta una `DELETE FROM Purge.RunBatchProgress WHERE RunId = @RunId` in testa
all'inserimento, per `InitializeBatchProgress` e `InitializeOrphanBatchProgress`.
È sicura perché il planning gira sempre prima che l'esecuzione cominci.

### 3. La coda del collettivo falliva sui file con righe scartate
`CollectiveOrderGroupOrder.OrderId` è nullable (caso C3): le righe respinte in
validazione non diventano mai `Order`, non entrano nello staging e il gruppo 3
non le tocca. La `DELETE` su `CollectiveOrderGroup` andava quindi in violazione
di FK per ogni file che contenesse anche una sola riga scartata — non un caso
raro, visto che le colonne `ValidationErrors` e `ValidationWarnings` esistono
proprio per quello.

Aggiunti due statement in testa a `CollectiveTailTables`:
`CollectiveOrderGroupOrderHistoryResidue` e `CollectiveOrderGroupOrderResidue`.

## Minori

### 4. Un collettivo problematico faceva fallire l'intero run
`CollectiveTailExecutor` lanciava `InvalidOperationException` sui componenti non
completati e l'eccezione risaliva fino all'orchestratore. Ora il collettivo viene
abbandonato, marcato `Failed` in `RunCandidateCollective` e il ciclo prosegue,
coerentemente con il trattamento delle slice.

### 5. `DateTimeOffset.Now` al posto del `TimeProvider`
La finestra operativa non era testabile proprio nel componente in cui il
comportamento al confine conta.

## Aggiunto

### `db/004_verify_fk.sql`
Cinque query che confrontano le FK reali del database con `PurgeTopology`:
delete rule effettive, cascate non previste, vincoli untrusted, e soprattutto
tabelle figlie di `Order` e `OrderHistory` assenti dai gruppi 1 e 3.

Tutto il disegno poggia sullo snapshot EF, che descrive il modello come
l'applicazione crede che sia. Se il database reale diverge, l'ordine topologico
va rifatto. **Da eseguire prima del merge.**

## Non verificato

Nessuna compilazione: l'ambiente non ha accesso a nuget.org. Restano necessari
un `dotnet build` su .NET 10 e l'esecuzione di `db/003_preflight.sql` e
`db/004_verify_fk.sql` sul database reale.

## V4.1 – Strategy refactoring safe

- Introdotta `IPurgeStrategy` con implementazioni dedicate per ogni scenario.
- Introdotto `PurgeStrategyResolver` tramite DI, eliminando gli switch distribuiti dal motore.
- Mantenuti integralmente gli statement di selezione/espansione che popolano le tabelle `Purge.*`.
- `PreDeleteValidator`, `BatchPlanner`, `SliceExecutor` e `RetentionOrchestrator` consumano le capability della strategy.
- `RetentionPolicy` separa le regole di eleggibilita' dal comportamento di purge.
- Corretto un problema V3 nel `BatchPlanner`: le temp table SQL Server sono connection-scoped; lettura e bulk copy ora usano la stessa connessione.
- `PurgeOptions.Strategies` resta intenzionalmente presente: definisce quali strategie eseguire e in quale ordine. Non rappresenta piu' la registrazione tecnica delle implementazioni.
