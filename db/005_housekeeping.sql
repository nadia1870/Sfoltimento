/* =====================================================================
   Housekeeping delle tabelle di controllo — v11 §7.4-7.6
   Aggiunge il marcatore di avvenuta pulizia dello staging.
   Idempotente.
   ===================================================================== */

IF COL_LENGTH('Purge.PurgeRun', 'StagingPurgedOn') IS NULL
    ALTER TABLE Purge.PurgeRun ADD StagingPurgedOn DATETIMEOFFSET NULL;
GO

/* Indice sui run ancora da ripulire. Filtrato su StagingPurgedOn IS NULL,
   quindi resta minuscolo: contiene solo i run non ancora trattati, non
   l'intera storia. Senza, l'individuazione dei candidati sarebbe una
   scansione ripetuta a ogni ciclo del cronjob.                          */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PurgeRun_Housekeeping'
               AND object_id = OBJECT_ID('Purge.PurgeRun'))
    CREATE NONCLUSTERED INDEX IX_PurgeRun_Housekeeping
        ON Purge.PurgeRun (CompletedOn, StartedOn) INCLUDE (Phase)
        WHERE StagingPurgedOn IS NULL;
GO

/* ---------------------------------------------------------------------
   Occupazione corrente dello staging. Da osservare periodicamente: una
   crescita costante indica che le finestre di conservazione sono troppo
   ampie rispetto al ritmo di esecuzione.
   --------------------------------------------------------------------- */
SELECT
    RunConStaging = (SELECT COUNT(*) FROM Purge.PurgeRun WHERE StagingPurgedOn IS NULL),
    Ordini        = (SELECT COUNT_BIG(*) FROM Purge.RunCandidateOrder),
    Storici       = (SELECT COUNT_BIG(*) FROM Purge.RunCandidateOrderHistory),
    Collettivi    = (SELECT COUNT_BIG(*) FROM Purge.RunCandidateCollective);

/* Spazio fisico occupato dalle due tabelle che contano. */
SELECT
    Tabella = QUOTENAME(s.name) + '.' + QUOTENAME(t.name),
    Righe   = p.rows,
    MB      = CAST(SUM(a.total_pages) * 8.0 / 1024 AS DECIMAL(10,1))
FROM sys.tables t
JOIN sys.schemas s   ON s.schema_id = t.schema_id
JOIN sys.indexes i   ON i.object_id = t.object_id
JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id = i.index_id
JOIN sys.allocation_units a ON a.container_id = p.partition_id
WHERE s.name = 'Purge'
GROUP BY s.name, t.name, p.rows
ORDER BY MB DESC;
