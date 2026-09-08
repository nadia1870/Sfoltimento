# Retention Purge – Hardening V2

## Correzioni principali

1. **Distributed/single-instance lock**
   - SQL Server `sp_getapplock`, `Exclusive`, `Session`, timeout 0.
   - Il lock resta legato alla connessione fino al termine del ciclo.

2. **State machine / recovery**
   - Le fasi rappresentano la prossima fase da eseguire.
   - Il passaggio di fase avviene dopo il successo dell'operazione.
   - Selection, expansion e planning sono idempotenti per consentire recovery dopo crash.

3. **Collective**
   - Aggiunto `CollectiveTailExecutor`.
   - La coda viene eseguita solo quando tutti i componenti sono stati cancellati.
   - `RunCandidateCollective.State` diventa `Deleted` nella stessa transazione della tail.
   - La tail rispetta la finestra operativa.

4. **OrphanHistory**
   - Aggiunto un planner dedicato.
   - Gli orphan sono assegnati a `RunBatchProgress`.
   - `SliceExecutor` utilizza statement dedicati per le foglie e `OrderHistory`.

5. **Batch planning**
   - Corretto il calcolo di `SliceCount` con aggregati oversized.
   - Planning degli orphan eseguito su connessione di lettura separata dalla connessione che ospita la temp table.

6. **Idempotenza Collective selection**
   - Inseriti `NOT EXISTS` per evitare duplicati in caso di recovery.

7. **Runtime**
   - Core e Host target `net10.0`.
