/* =====================================================================
   Verifiche preliminari — DA ESEGUIRE PRIMA DI CONFIGURARE IL MOTORE
   Rif. v10 §10.2, §10.5, §10.7, §6.3, §19. Nessuna modifica ai dati.
   ===================================================================== */

/* 1. ExecutionDate non valorizzata (§10.2).
      datetime2 NOT NULL riceve 0001-01-01 se l'applicazione non la imposta:
      quel valore supera QUALSIASI soglia di retention. Se il conteggio e'
      maggiore di zero, investigare la causa prima di attivare il motore.  */
SELECT Sentinelle = COUNT(*)
FROM PaymentOrder.[Order] WHERE ExecutionDate < '1900-01-01';

/* 2. Collettivi senza data di esecuzione (§10.7).
      ExecutionDate e' nullable su CollectiveOrder, a differenza di Order:
      con NULL il predicato e' sempre falso e il collettivo non verrebbe mai
      sfoltito, bloccando anche i suoi componenti.                          */
SELECT SenzaData = COUNT(*)
FROM PaymentOrder.CollectiveOrder WHERE ExecutionDate IS NULL;

/* 3. Distribuzione delle revisioni per ordine (§6.3).
      Determina MaxRowsPerBatch e rivela gli ordini oversized.             */
SELECT Revisioni = cnt, Ordini = COUNT(*)
FROM (SELECT o.Id, cnt = COUNT(oh.Id)
      FROM PaymentOrder.[Order] o
      LEFT JOIN PaymentOrder.OrderHistory oh ON oh.OrderRefId = o.Id
      GROUP BY o.Id) x
GROUP BY cnt ORDER BY cnt DESC;

/* 4. Eta' degli ordini abbandonati (§10.5).
      Attenzione a PartiallyAuthorised: almeno una firma e' gia' apposta.   */
SELECT StatusCode, Mesi = DATEDIFF(MONTH, CreationDate, GETDATE()), Ordini = COUNT(*)
FROM PaymentOrder.[Order]
WHERE StatusCode IN ('Created','PartiallyAuthorised')
GROUP BY StatusCode, DATEDIFF(MONTH, CreationDate, GETDATE())
ORDER BY StatusCode, Mesi DESC;

/* 5. Valori di stato realmente presenti (C6): nessuna FK li garantisce.    */
SELECT StatusCode, Ordini = COUNT(*)
FROM PaymentOrder.[Order] GROUP BY StatusCode ORDER BY Ordini DESC;

/* 6. Storici orfani (C1).                                                  */
SELECT Orfani = COUNT(*) FROM PaymentOrder.OrderHistory WHERE OrderRefId IS NULL;
