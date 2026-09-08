# V8 — Esclusione dei collettivi anomali e bisezione delle slice

Due interventi sui bordi del disegno, registrati come D-10 e D-11 in
`docs/decisioni.md`. Nessuno dei due tocca la policy: l'impronta di
`PurgePolicy` non cambia e le approvazioni esistenti restano valide.

## D-10 — Collettivi

- `CollectiveStrategy.SelectAsync` esclude e censisce, prima di leggere i
  componenti, i collettivi che avrebbero fatto fallire il run in `Validating`:
  `ComponentHasModel`, `AmbiguousMembership`, `CrossRef:<tabella di storico>`.
- Il `throw` sull'appartenenza ambigua diventa una post-condizione.
- `CountCollectiveAggregate` conta solo i collettivi `Selected`: il dry-run
  non include più le righe dei collettivi esclusi.
- Il report del dry-run elenca i collettivi esclusi per motivo.
- `PurgeMetrics.CandidatesExcluded` viene ora alimentata.

## D-11 — Slice

- `db/012_slice_split.sql`: `RunBatchProgress.ParentBatchNo`, `SplitDepth`,
  indice `IX_RBP_Parent`. **Va applicato**: `SchemaVerifier` rifiuta di partire
  senza.
- `SliceResult.Splittable`, alimentato da `SliceExecutor` tramite
  `SqlErrors.IsDataIntegrity` (547, 2627, 2601).
- `IBatchWorkProvider.SplitAsync`, `PurgeRunStore.SplitSliceAsync`,
  `RetentionSql.SplitSlice`.
- `BatchExecutionCoordinator`: su `Fatal` divisibile divide invece di
  abbandonare, fino a `Purge:MaxSplitDepth` (default 10, zero disattiva).
- `SliceInfo.SplitDepth`, nuovo stato `Split` in `RunBatchProgress`, metrica
  `purge.slices_split`, evento di log `PurgeSliceSplit`. L'evento
  `PurgeSliceAbandoned` di `SliceExecutor` diventa `PurgeSliceFailed`: la
  decisione di abbandonare non è più sua.

## Test

- Unit: `CollectiveExclusionContractTests`, casi nuovi in `SqlErrorsTests`,
  `SliceExecutorTests`, `BatchExecutionCoordinatorTests`.
- Integrazione: `CollectiveExclusionTests`, `SliceSplitTests`.
- I fake di `IBatchWorkProvider` implementano `SplitAsync`.

## Da fare a mano dopo il merge

- Applicare `012_slice_split.sql` su ogni database prima del deploy.
- Aggiornare gli alert costruiti su `PurgeSliceAbandoned`: l'evento con quel
  nome non esiste più; l'abbandono si legge da `RunBatchProgress` o dalla
  metrica `purge.slices_abandoned`, che è invariata.
