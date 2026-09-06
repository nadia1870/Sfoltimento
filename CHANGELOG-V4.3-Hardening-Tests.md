# V4.3 – Hardening: State Machine / Resume / Pacing

## Obiettivo

Aggiungere la copertura dei tre punti identificati nella code review senza introdurre nuove astrazioni architetturali.

## Test aggiunti

### MUST
- `MUST_Executing_Stay_then_next_invocation_resumes_pending_slice`
  - porta un run fino a `Executing`;
  - chiude la finestra operativa;
  - verifica che l'orchestratore termini lasciando il run in `Executing` e le slice `Pending`;
  - riapre la finestra;
  - verifica che la successiva invocation riprenda lo stesso checkpoint e termini in `Completed`.

### IMPORTANT
- `IMPORTANT_cancellation_preserves_the_current_phase_checkpoint`
  - verifica che una cancellation non faccia avanzare la fase persistita.
- `IMPORTANT_successful_phase_transition_is_persisted_before_next_phase_runs`
  - verifica che una transizione completata venga persistita prima dell'ingresso nella fase successiva;
  - la cancellation nella fase successiva lascia il run sulla nuova fase.

### SHOULD
- `SHOULD_coordinator_does_not_fetch_work_after_the_final_slice_without_extra_pacing`
  - verifica che una singola slice non introduca un `InterSliceDelay` dopo l'ultima slice.

## Modifica implementativa

`BatchExecutionCoordinator` usa un piccolo look-ahead sulla prossima slice:

1. esegue la slice corrente;
2. recupera la prossima slice;
3. applica `InterSliceDelay` solo se esiste una prossima slice;
4. se non esiste, termina immediatamente.

Il provider è read-only rispetto al checkpoint: il look-ahead non riserva né modifica la slice.

## Architettura

Nessuna nuova interfaccia o livello architetturale è stato introdotto. Restano invariati:

`RetentionOrchestrator → IPurgePhase → ExecutingPhase → IBatchExecutionCoordinator → IBatchWorkProvider / IBatchExecutor`.

## Build / test

Nel presente ambiente `dotnet` non è disponibile, quindi la suite non è stata eseguita. Sono state effettuate verifiche statiche sui file modificati.
