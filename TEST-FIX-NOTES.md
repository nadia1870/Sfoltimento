# Test fix – V5 BatchExecutionCoordinator

Ho verificato i test presenti nella repository GitHub.

Il problema segnalato è un errore di composizione DI: tutte le suite che usano
PurgeDatabaseFixture condividono lo stesso ServiceProvider e registrano
ExecutingPhase, ma non registrano la nuova BatchExecutionCoordinator.

Fix applicato:
- aggiunto `using OSM.PaymentOrder.Purge.Engine.BatchExecution;`
- aggiunto `services.AddSingleton<BatchExecutionCoordinator>();`

Questo è sufficiente per il grafo DI della fixture perché gli altri test
recuperano le phases dal container oppure usano RetentionOrchestrator.

Suite verificate sulla repository:
- RetentionResilienceTests
- RetentionInvariantsTests
- AbandonedAndLockTests

Non risultano altre istanziazioni dirette di ExecutingPhase nei test pubblicati.

Nota: il test `Ordine_sotto_soglia_non_viene_cancellato` non aveva un difetto
funzionale: falliva prima dell'esecuzione, durante la costruzione del container.
