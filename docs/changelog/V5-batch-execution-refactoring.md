# V5 – Batch Execution Refactoring

## Cosa cambia

1. `ExecutingPhase` viene ridotta a una vera fase del workflow.
   Non contiene più il loop delle slice, retry, abandon, pacing e metriche.

2. La responsabilità viene estratta in `BatchExecutionCoordinator`.

3. Il contratto `BatchExecutionResult` distingue:
   - run completato;
   - finestra operativa terminata, quindi `Stay()`.

4. `SliceExecutor` resta il confine dell'esecuzione SQL atomica.

5. `CollectiveTailPhase` non viene eliminata in questo passaggio:
   è ancora utile come compatibilità per run legacy. I nuovi run non devono
   raggiungerla perché il Collective viene cancellato atomicamente nella slice.

## Flusso risultante

RetentionOrchestrator
    -> ExecutingPhase
        -> BatchExecutionCoordinator
            -> PurgeRunStore.NextPendingSliceAsync
            -> SliceExecutor.ExecuteAsync
                -> SQL transaction / DELETE / checkpoint

Questo separa la semantica della fase dalla politica di esecuzione dei batch
e lascia un seam pulito per una futura implementazione RabbitMQ.
