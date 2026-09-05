# Sfoltimento — motore di retention per OSM.PaymentOrder

Sfoltimento periodico dei dati del dominio `OSM.PaymentOrder` su SQL Server.
Copre il solo flusso di **retention**: gli scenari on-demand (numero relazione,
PaymentId) non sono implementati.

Target `net10.0`. Il motore usa SQL raw e non referenzia alcun `DbContext`:
è questo che consente di ospitarlo su un runtime diverso da quello
dell'applicazione, ferma a EF Core 3.1.

## Struttura

```
db/    000_install_purge.sql   installazione autonoma dello schema Purge
       001_purge_schema.sql    schema (incluso in 000)
       002_indexes.sql         indici su PaymentOrder — valutare ONLINE = ON
       003_preflight.sql       verifiche sui dati, DA ESEGUIRE PRIMA
       004_verify_fk.sql       confronto FK reali vs topologia attesa

src/OSM.PaymentOrder.Purge/         motore
src/OSM.PaymentOrder.Purge.Host/    cronjob ed esecuzione singola
```

## Avvio

```bash
sqlcmd -S <host>,<porta> -d <database> -i db/000_install_purge.sql
sqlcmd -S <host>,<porta> -d <database> -i db/003_preflight.sql

dotnet run --project src/OSM.PaymentOrder.Purge.Host              # servizio con cron
dotnet run --project src/OSM.PaymentOrder.Purge.Host -- once      # esecuzione singola
```

## Configurazione locale

`appsettings.json` contiene solo segnaposto. La configurazione reale va in
`appsettings.Development.json` (ignorato da git — vedere il file `.example`)
oppure in variabile d'ambiente:

```bash
set PURGE_ConnectionStrings__PaymentOrder=Server=...;Database=...;Integrated Security=true;TrustServerCertificate=true
```

Doppio underscore come separatore di sezione.

## Prima di eseguire davvero

`DryRun` è a `true`. Per il dry-run **non serve** il permesso di `DELETE` su
`PaymentOrder`: eseguirlo con un'utenza in sola lettura rende l'assenza di
cancellazioni una proprietà strutturale e non una promessa del codice.

Il `GRANT DELETE` va concesso solo dopo l'approvazione del report.

## Punti di attenzione

- `Sql/PurgeTopology.cs` non va riordinato: l'ordine dei quattro gruppi è il
  contratto di correttezza. Ogni `*History` di dettaglio referenzia con
  `Restrict` sia `OrderHistory` sia il dettaglio corrente.
- `SliceExecutor` esegue il rollback se la `DELETE` su `Order` cancella meno
  righe delle attese: senza, resterebbe un ordine privo di storico e dettagli.
- Le foreign key `Restrict` non vanno mai disabilitate: sono la rete di
  sicurezza finale.
- `MaxRowsPerBatch` va tarato sui dati reali (verifica 3 di `003_preflight.sql`).

## Punti aperti

| ID | Punto | Interlocutore |
|---|---|---|
| PA-3 | I 5 anni decorrono dall'operazione o dalla chiusura d'esercizio? | Compliance / Legal |
| PA-4 | 5 anni sono sufficienti? | Compliance / Legal |
| PA-5 | Serve un meccanismo di legal hold? | Legal |
| PA-7 | Collettivi con `ExecutionDate` NULL: oggi esclusi e censiti | Business |
| PA-21 | Soglia degli ordini abbandonati (`AbandonedEnabled` è `false`) | Business / Compliance |
