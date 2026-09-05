/* Verifica che nessun Collective del run sia distribuito su piu' BatchNo. */
SELECT RunId, CollectiveOrderId, COUNT(DISTINCT BatchNo) AS BatchCount
FROM Purge.RunCandidateOrder
WHERE CollectiveOrderId IS NOT NULL
GROUP BY RunId, CollectiveOrderId
HAVING COUNT(DISTINCT BatchNo) > 1;
GO

/* Verifica che il BatchNo del Collective coincida con quello di tutti i suoi ordini. */
SELECT rc.RunId, rc.CollectiveOrderId, rc.BatchNo AS CollectiveBatchNo,
       MIN(co.BatchNo) AS MinOrderBatchNo, MAX(co.BatchNo) AS MaxOrderBatchNo
FROM Purge.RunCandidateCollective rc
JOIN Purge.RunCandidateOrder co
  ON co.RunId = rc.RunId AND co.CollectiveOrderId = rc.CollectiveOrderId
GROUP BY rc.RunId, rc.CollectiveOrderId, rc.BatchNo
HAVING rc.BatchNo IS NULL OR MIN(co.BatchNo) <> rc.BatchNo OR MAX(co.BatchNo) <> rc.BatchNo;
GO
