# V6 — traccia di audit e selezione a pagine

Due interventi indipendenti, applicabili separatamente. Il primo chiude un buco
di tracciabilità, il secondo il punto in cui il motore si sarebbe rotto al primo
run su dati storici.

Richiede `db/008_audit_trail.sql` e l'esecuzione di `db/002_indexes.sql`, che
contiene un indice nuovo. Senza il primo, il motore non parte: `SchemaVerifier`
ora controlla anche le colonne.

## 1. `Purge.PurgeAudit` viene finalmente scritta

La tabella esisteva nello schema fin dalla prima versione, ma nessuno la
popolava: `PurgeRunStore.RecordAuditAsync` non era chiamata da alcun percorso.
La traccia di cosa fosse stato cancellato viveva solo nei log e in
`RunBatchProgress.ActualDeletedRows`, aggregato per slice e senza dettaglio per
tabella. Per un purge di ordini di pagamento è precisamente ciò che un revisore
chiede.

La scrittura sta ora in `SliceExecutor`, nella **stessa transazione** delle
`DELETE` e del checkpoint. Se la slice va in rollback la traccia sparisce con
essa: non può esistere una riga di audit che dichiara cancellate righe ancora
presenti a database, che è l'unico modo in cui un audit può fare danno.

Un solo `INSERT` multi-riga prima del commit, non uno per tabella: la
transazione tiene lock sulle tabelle di dominio, e allungarla di venticinque
andate e ritorno vanificherebbe il lavoro fatto sul dimensionamento delle slice.
Le tabelle a zero righe non entrano nell'audit — sono la maggioranza in ogni
slice, perché un ordine ha un solo tipo di dettaglio.

`RecordAuditAsync` è stata rimossa. Scriveva su una connessione propria, quindi
fuori transazione: tenerla avrebbe lasciato in giro il modo sbagliato di
popolare la tabella.

## 2. `vDryRunVsActual` confronta qualcosa

La view faceva join fra `DryRunReport` e `PurgeAudit` sullo stesso `RunId`, ma
un dry-run chiude in `PlanningPhase` e il run reale della notte dopo ha un
`RunId` diverso, che non scriveva mai in `DryRunReport`. Anche popolando
l'audit, la view sarebbe rimasta a `Effettivo = 0` su ogni riga.

`PlanningPhase` produce ora il conteggio previsionale anche per i run reali,
prima della parte distruttiva. Controllato da `Purge:AuditBaselineEnabled`,
attivo di default: disattivarlo fa perdere il riscontro a posteriori, non
l'audit.

`PurgeAudit` prende `BatchNo` e la view aggrega, perché l'audit è append-only
con una riga per `(RunId, BatchNo, tabella)`: senza `GROUP BY` il `LEFT JOIN`
moltiplicherebbe le righe del previsionale.

`DryRunReporter` cancella prima di inserire. Il planning viene rieseguito se il
processo cade in quella fase, e due baseline per tabella produrrebbero uno
scostamento inventato — stessa ragione della `DELETE` già presente in
`InitializeBatchProgress`.

## 3. Selezione ed espansione a pagine

Le selezioni erano un unico `INSERT..SELECT` sull'intera tabella `Order`. Su
dati storici quello statement apre una transazione implicita lunga, fa crescere
il log e tiene lock proprio nel punto in cui tutto il resto del motore evita di
prenderne.

Il disegno della paginazione è spiegato in `docs/decisioni.md`, ed è
controintuitivo: **non** va sostituito con il loop ovvio.

`SelectionBatchSize` regola la dimensione della pagina, 4.000 di default, sotto
la soglia di lock escalation di SQL Server.

## 4. Espansione e pesi in un ciclo solo

Erano due passate distinte sull'intero set di candidati. Il peso di un ordine
dipende solo dai propri storici, quindi si calcola sulla stessa pagina appena
espansa: una passata invece di due, e nessun istante in cui l'intero set è sotto
`UPDATE`.

## 5. `IX_StandingOrder_Purge`

La soglia dei piani ricorrenti è su `StandingOrder.LastExecutionDate`, che non
aveva indice: la selezione scandiva l'intera tabella, e la paginazione a chiave
non avrebbe avuto una chiave su cui cercare. Su tabelle grandi usare
`ONLINE = ON`.

## 6. `SqlParam.Typed`

`AddWithValue` inferisce `SqlDbType.DateTime` per un `DateTime`, e il minimo di
quel tipo è il 1753: la data sentinella da cui parte la paginazione sarebbe
esplosa lato client prima di raggiungere il server. Introdotto il parametro con
tipo dichiarato e usato dove il tipo conta.

## Non toccato

I collettivi restano su statement singoli, non paginati, per la ragione spiegata
in `docs/decisioni.md`. La topologia, l'ordine degli statement di slice, la
macchina a stati e l'housekeeping sono invariati.

## Resta aperto

- Il legal hold (PA-5) non esiste ancora, e va nella selezione: ogni modifica
  futura ai predicati richiede di rifare l'approvazione del dry-run.
- Un fallimento infrastrutturale marca il run `Failed`, che è terminale e non
  riprendibile: manca la distinzione fra guasto ed errore logico.
- Il lock applicativo non ha heartbeat: se la sessione cade, il lock si rilascia
  e nessuno se ne accorge.
- Le metriche sono definite ma non esportate.
- Un run con slice abbandonate chiude in `Completed`: manca
  `CompletedWithErrors`.
