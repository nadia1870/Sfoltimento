# Runbook — primo dry-run in produzione

Procedura per la prima esecuzione del motore di retention su un database di
produzione, in sola simulazione. Al termine nessuna riga di `PaymentOrder` è
stata cancellata: l'unico effetto sono le tabelle di controllo nello schema
`Purge`, che servono all'analisi e che il motore ripulisce da solo.

Tempo indicativo: mezza giornata di preparazione (passi 0–3, con una finestra
di manutenzione per gli indici), una notte per il dry-run (passo 4), mezza
giornata di analisi (passi 5–6).

---

## 0. Prima di toccare qualsiasi cosa

### Persone

| Ruolo | Serve per |
|---|---|
| DBA | passi 1–2 (schema, indici, permessi), passo 5 (lettura risultati) |
| Chi gestisce lo scheduler (UC4) | passo 4 |
| Referente applicativo di OSM.PaymentOrder | passo 5, interpretazione delle anomalie |
| Compliance / Legal | conferma di `RetentionYears` e `AnchorMode` (PA-3, PA-4) **prima** del passo 3 |

### Cosa deve esistere

- Un host con il runtime .NET 10 e il build della soluzione
  (`dotnet publish -c Release src/OSM.PaymentOrder.Purge.Host`). Il processo
  legge `appsettings.json` dalla cartella dell'eseguibile, non dalla cartella
  corrente.
- Connettività dall'host al database di produzione sulla porta SQL.
- Un'utenza SQL dedicata al purge (vedi passo 2). **Non** l'utenza
  dell'applicazione: il dry-run deve girare con un'utenza che non ha
  `DELETE` su `PaymentOrder`, così l'assenza di cancellazioni è una proprietà
  dei permessi e non una promessa del codice (D-4).
- Il repository alla versione da collaudare, con `db/` e `docs/` accessibili
  al DBA.

### Cosa NON fa questa procedura

- Non concede `DELETE` su `PaymentOrder`.
- Non esegue `purge once --delete`.
- Non approva la policy. L'approvazione (`purge approve`) è un passo
  separato, dopo l'analisi, e non è coperto qui se non come rimando.

---

## 1. Schema `Purge` e verifiche preliminari (DBA)

Tutti gli script sono in `db/`. Eseguirli con `sqlcmd` **sul database di
OSM.PaymentOrder**, nell'ordine indicato. `000` si rifiuta di girare se il
nome del database non contiene `PaymentOrder`.

```
sqlcmd -S <host>,<porta> -d <database> -E -i db/000_install_purge.sql   -o 000.out
sqlcmd -S <host>,<porta> -d <database> -E -i db/004_verify_fk.sql       -o 004.out
sqlcmd -S <host>,<porta> -d <database> -E -i db/003_preflight.sql       -o 003.out
sqlcmd -S <host>,<porta> -d <database> -E -i db/020_analisi_pre_dry_run.sql -o 020.out
```

Cosa controllare in ciascun output:

**000** — l'ultimo result set deve dire `Esito = OK`, `Tabelle = 9`,
`Viste = 1`. Lo script è idempotente: se qualcosa è andato storto si può
rieseguire. In alternativa, con un'utenza che ha permessi DDL,
`purge migrate` fa la stessa cosa dal programma e registra in
`Purge.SchemaVersions` cosa ha applicato; `purge migrate --status` dice se
manca qualcosa (esce con 5) e non modifica niente.

**004** — è la verifica più importante: confronta le foreign key *reali* con
la topologia che il motore assume. Attese: `NO_ACTION` su tutte le FK
entranti nell'aggregato `Order`, `CASCADE` **solo** su `Model.OrderId`.
Qualunque differenza — una FK assente, una regola diversa, una tabella che
referenzia `Order` e non è in `PurgeTopology` — **ferma la procedura**: il
motore non va eseguito, nemmeno in dry-run, finché la topologia non è
allineata.

**003** — sei conteggi. In particolare:
- *Sentinelle* (`ExecutionDate < 1900`): se > 0, quegli ordini non saranno
  mai eleggibili. Non blocca, ma va capito perché esistono.
- *Distribuzione revisioni*: il valore massimo dice quanto può pesare un
  singolo ordine. Serve al passo 3 per `MaxRowsPerBatch`.
- *Stati presenti*: gli stati terminali attesi sono `Executed`, `Cancelled`,
  `Deleted`, `Refused`, `Extincted`. Uno stato terminale con un nome diverso
  non verrebbe mai sfoltito.

**020** — analisi estesa, sola lettura, in `READ UNCOMMITTED`. Produce
l'ordine di grandezza di ciò che il dry-run selezionerà, la distribuzione dei
pesi, gli aggregati oversized, le anomalie note (modelli, collettivi
bloccati, storici orfani) e lo stato degli indici. Va conservato: è la
baseline con cui confrontare il report del dry-run al passo 5. Può durare
alcuni minuti su tabelle grandi: eseguirlo fuori dall'orario di punta o su una
replica in lettura.

### Indici (`002_indexes.sql`)

Senza gli indici di `002` la selezione scandisce l'intera tabella `Order` a
ogni pagina. Su volumi reali il dry-run può durare ore e produrre letture
pesanti in concorrenza con l'operatività. Gli indici vanno creati **prima**
del dry-run, in una finestra di manutenzione, con `ONLINE = ON` se l'edizione
lo consente. Sono cinque indici filtrati, quindi piccoli rispetto alle
tabelle. La sezione *Indici* di `020` dice quali esistono già.

---

## 2. Utenza e permessi (DBA)

Creare o individuare l'utenza del purge e concedere:

```sql
GRANT SELECT ON SCHEMA::PaymentOrder TO [<UTENZA_PURGE>];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::Purge TO [<UTENZA_PURGE>];
```

Niente altro. In particolare **non** `DELETE` su `PaymentOrder`: verrà
concesso, se e quando, solo dopo l'approvazione del report.

Verifica: connettersi con quell'utenza ed eseguire

```sql
SELECT HAS_PERMS_BY_NAME('PaymentOrder.[Order]', 'OBJECT', 'DELETE') AS PuoCancellare,
       HAS_PERMS_BY_NAME('PaymentOrder.[Order]', 'OBJECT', 'SELECT') AS PuoLeggere,
       HAS_PERMS_BY_NAME('Purge.PurgeRun',        'OBJECT', 'INSERT') AS PuoScrivereStaging;
```

Atteso: `0, 1, 1`.

---

## 3. Configurazione del motore

`appsettings.json` accanto all'eseguibile contiene solo segnaposto. I valori
reali vanno in `appsettings.Development.json` (mai committato) oppure in
variabili d'ambiente con prefisso `PURGE_` e doppio underscore come
separatore. Per il dry-run bastano:

```
PURGE_ConnectionStrings__PaymentOrder=Server=<host>,<porta>;Database=<database>;User Id=<UTENZA_PURGE>;Password=...;Encrypt=true;TrustServerCertificate=false
PURGE_Purge__RetentionYears=5
PURGE_Purge__AnchorMode=FiscalYearEnd
```

Note:

- `TrustServerCertificate=true` va bene in sviluppo, non in produzione: usare
  un certificato valido o esplicitamente accettare il rischio.
- `RetentionYears` e `AnchorMode` **sono la policy**: la loro impronta viene
  scritta sul run e l'approvazione varrà solo per quella combinazione.
  Cambiarli dopo il dry-run invalida il dry-run. Vanno confermati da
  Compliance prima di lanciare.
- `AbandonedEnabled` resta `false` (PA-21 aperto).
- `DryRun` in configurazione **non ha effetto** su `purge once`: la modalità
  la decide la riga di comando. È lì apposta.
- `MaxRowsPerBatch` (default 3000): deve stare sopra il peso del p99 degli
  ordini visto in `020` (peso = 1 + 2 × revisioni). Gli ordini oltre il tetto
  diventano slice dedicate, non è un errore, ma se sono molti conviene
  alzarlo. Restare sotto ~5000 per non innescare la lock escalation.
- `WindowStart`/`WindowEnd` (default 01:00–05:00) sono nell'**ora locale
  dell'host**. Su un container è spesso UTC: verificare con `date` sull'host.

Prova della configurazione senza toccare il database:

```
purge once
```

Deve uscire con codice **2** e il messaggio "Specificare la modalità". Se
esce con un errore sulla connection string, la variabile non è stata letta.

---

## 4. Esecuzione del dry-run

### Quando

Nella finestra notturna configurata. `purge once` rispetta la finestra anche
in modalità singola: lanciato alle 15:00 con finestra 01–05 esce subito con
codice **4** senza fare nulla. Per un lancio manuale di giorno serve
`--no-window`, esplicito.

Raccomandazione per la prima volta: **due lanci**.

**4a. Fumo, di giorno, su una sola strategia leggera.** Verifica connessione,
permessi, schema e formato del report senza aspettare la notte:

```
purge once --dry-run OrphanHistory --no-window > dryrun-fumo-$(date +%Y%m%d).log 2>&1
echo "exit=$?"
```

Atteso: codice 0, nel log `PurgeRunStarted`, `PurgeSelectionCompleted` (o
`PurgeSelectionEmpty` se non ci sono orfani), `PurgeDryRunReport`,
`PurgeRunCompleted`. Durata: minuti.

**4b. Completo, di notte, tutte le strategie.** Pianificato dallo scheduler
all'ora di apertura della finestra, oppure a mano con `nohup`:

```
purge once --dry-run > dryrun-$(date +%Y%m%d).log 2>&1
echo "exit=$?"
```

Il processo esegue in sequenza `Terminated`, `StandingOrders`, `Collective`,
`OrphanHistory`. Ogni strategia è un run separato con il proprio `RunId`,
stampato su stdout all'avvio:

```
Terminated       run 3f2a...  DryRun=True
```

Conservare il log: contiene il report testuale di ogni strategia.

### Codici di uscita

| Codice | Significato | Cosa fare |
|---|---|---|
| 0 | Tutte le strategie concluse | Passo 5 |
| 1 | Almeno una strategia è fallita | Cercare `PurgeRunFailed` o `PurgeValidationFailed` nel log; vedere `Purge.ValidationFinding` (021, sezione B). Un run `Failed` non riprende: il lancio successivo ne crea uno nuovo |
| 2 | Configurazione o schema | Il log dice cosa manca (variabile, tabella, colonna, migrazione) |
| 4 | Fuori finestra | Rilanciare nella finestra o con `--no-window` |
| 5 | Una fase ha superato la finestra oltre la tolleranza | Non è un errore dei dati: il run riprende dal checkpoint al lancio successivo. Ma è il segnale che la selezione è lenta: verificare gli indici di `002` |
| 130 | Interrotto con Ctrl-C | Il run riprende dal checkpoint al lancio successivo |

Un run interrotto (5, 130, o caduta della connessione) **non va cancellato**:
al lancio successivo `FindResumableAsync` lo riprende dalla fase in cui era.
Solo un run `Failed` è definitivo.

### Cosa succede sul database durante il dry-run

Letture su `PaymentOrder` a pagine da `SelectionBatchSize` righe (default
4000), sotto la soglia di lock escalation, con `DEADLOCK_PRIORITY LOW`.
Scritture solo su `Purge.*`: una riga per ordine candidato e una per
revisione. Su volumi reali lo staging può arrivare a diversi GB: verificare lo
spazio prima (020, sezione *Dimensioni*). Il dry-run non crea
`RunBatchProgress` e non tocca `PurgeAudit`.

---

## 5. Raccolta e analisi dei risultati

Eseguire, con qualsiasi utenza in lettura:

```
sqlcmd -S <host>,<porta> -d <database> -E -i db/021_analisi_post_dry_run.sql -o 021.out
```

Lo script legge l'ultimo run di ogni strategia e produce, per ciascuno:
fase finale ed eventuale errore, report previsionale per tabella, esito
delle validazioni, collettivi esclusi per motivo, statistiche delle slice,
aggregati oversized, distribuzione dei candidati per anno e per tipo di
dettaglio, occupazione dello staging.

### Criteri di lettura

**Va bene se:**

- Ogni run è in fase `Completed`. `PurgeSelectionEmpty` è un esito valido
  (zero candidati), non un errore.
- `Purge.ValidationFinding` non ha righe per quei run. Se ne ha, il run è
  `Failed` e le righe dicono quale regola (V1–V5) e su quale tabella.
- Il totale `Order` del report è dello stesso ordine di grandezza della
  stima di `020` (sezione *Eleggibilità*). Uno scarto grande in un senso o
  nell'altro va spiegato prima di procedere: di solito è una differenza di
  soglia o uno stato terminale con un nome inatteso.
- `Slice massima` non supera `MaxRowsPerBatch` **oppure** gli aggregati
  oversized sono pochi e identificati.
- `Ordini senza BatchNo` è zero.
- La distribuzione per anno dei candidati non contiene anni successivi alla
  soglia. Se ne contiene, la soglia non è quella che si credeva.

**Da esaminare con il referente applicativo:**

- *Collettivi esclusi*: ogni riga con `ExcludedReason` è un aggregato che
  resterà a database. `ComponentHasModel` e `CrossRef:*` sono anomalie dei
  dati; `ExecutionDateNull` è PA-7, ancora aperto.
- *Storici orfani*: sono righe irraggiungibili dall'applicazione. Un numero
  alto merita una spiegazione prima di cancellarle.
- *Ordini eleggibili referenziati da Model* (020): sono esclusi dal purge per
  costruzione, ma sono modelli che puntano a ordini di più di N anni fa.

**Ferma tutto se:**

- `004` aveva mostrato differenze e sono state ignorate.
- Un run è `Failed` con `LastError` che non è una validazione (eccezione,
  timeout): è un difetto o un guasto, non un dato.
- Il conteggio previsto è molto superiore a quello atteso dal business.

### Durata attesa dell'esecuzione reale

Il dry-run non la misura: si ferma prima delle cancellazioni. Una stima
prudente: numero di slice (021, *Statistiche slice*) × 1–3 secondi, più il
pacing. Con 10.000 slice sono 3–8 ore, cioè più di una finestra: il run
riprenderà dal checkpoint le notti successive. È previsto, ma va detto a chi
guarda i job.

---

## 6. Dopo l'analisi

Se il report è accettabile, l'approvazione si registra con

```
purge approve <RunId-del-dry-run> --by "<Nome Cognome>" --note "<riferimento al verbale>"
```

per **uno** dei run conclusi: ciò che si approva è la policy (impronta di
`RetentionYears`, `AnchorMode`, strategie, abbandoni), non il singolo run.
Il comando rifiuta un run non concluso, non dry-run, o eseguito con una policy
diversa da quella configurata adesso.

Solo dopo:

```sql
GRANT DELETE ON SCHEMA::PaymentOrder TO [<UTENZA_PURGE>];
```

e la pianificazione di `purge once --delete` come job separato. La prima
esecuzione reale merita un runbook suo.

Se il report **non** è accettabile, non c'è niente da annullare: lo staging
dei run conclusi viene rimosso dall'housekeeping dopo `StagingRetentionDays`
(default 7 giorni). Se serve conservarlo più a lungo per l'analisi, alzare
quel valore prima del lancio successivo. Un nuovo dry-run con una policy
diversa produce run nuovi con una nuova impronta.

---

## Appendice — cosa conservare

- `000.out`, `003.out`, `004.out`, `020.out`: stato del database prima.
- `dryrun-*.log`: log completo con i report testuali e i `RunId`.
- `021.out`: analisi del dry-run.
- I `RunId`: servono per `purge approve` e per rileggere `Purge.DryRunReport`
  e `Purge.ValidationFinding` finché lo staging esiste (`Purge.PurgeRun`,
  `DryRunReport` e `ValidationFinding` non vengono mai sfoltiti; il resto sì).
