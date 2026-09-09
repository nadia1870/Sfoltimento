# Runbook — prima esecuzione reale (`--delete`) in produzione

Procedura per la prima esecuzione che **cancella davvero** dati da
`PaymentOrder`.

Presuppone che sia già stato fatto tutto ciò che è in
`runbook-primo-dry-run.md`: schema `Purge` installato, indici di `002`
creati, dry-run concluso, report esaminato e accettato.

**Le cancellazioni non si annullano.** Le FK dell'aggregato sono
`NO ACTION` e il motore non le disabilita mai, ma questo protegge
dall'incoerenza, non dall'errore di valutazione: se la policy è sbagliata,
ciò che sparisce è sparito. L'unico rimedio è il ripristino da backup, e il
punto 1 esiste per questo.

---

## Prima di cominciare

### Chi deve esserci

| Ruolo | Quando |
|---|---|
| DBA | punti 1, 2, 5 (la prima notte in reperibilità) |
| Chi gestisce lo scheduler | punti 4 e 8 |
| Referente applicativo | punto 6, la mattina dopo |
| Chi ha approvato la policy | punto 3, di persona |

### Cosa deve essere già vero

- Un dry-run in stato `Completed` per ogni strategia che si intende
  eseguire, con il suo `RunId` annotato.
- Il report di quel dry-run esaminato e accettato per iscritto (verbale,
  ticket, mail: qualcosa a cui il campo `--note` possa fare riferimento).
- `RetentionYears` e `AnchorMode` confermati da Compliance e **non
  modificati** dopo il dry-run.
- Gli indici di `002_indexes.sql` presenti (sezione 6 di `020`).
- `AbandonedEnabled = false`, salvo decisione esplicita: PA-21 è aperto.

### Cosa NON fa questa procedura

- Non modifica lo schema: se `purge migrate --status` segnala qualcosa da
  applicare, ci si ferma e si torna al runbook precedente.
- Non cambia la policy. Cambiarla adesso invalida l'approvazione, e il gate
  se ne accorge.

---

## 1. Backup verificato (DBA)

Prima di concedere `DELETE`, non prima di eseguire.

- Un backup completo recente, **con restore provato** su un altro server.
  Un backup mai ripristinato è un'ipotesi, non una rete.
- Modello di recupero `FULL` con backup del log attivi, se si vuole poter
  tornare a un istante preciso. In `SIMPLE` si torna solo all'ultimo
  backup completo: va bene, purché sia una scelta consapevole e scritta.
- Annotare l'orario dell'ultimo backup e l'LSN o l'ora di ripristino
  raggiungibile. Se qualcosa va storto la notte, è il primo dato che serve.

**Spazio del log.** Il purge cancella in transazioni corte ma numerose, e
ogni riga passa dal log. Con `FULL`, se i backup del log sono radi, il file
cresce fino alla dimensione dei dati cancellati. Prima della prima notte:
verificare lo spazio libero (sezione 1 di `020`) e, se serve, infittire i
backup del log durante la finestra.

---

## 2. Permessi (DBA)

Solo ora, e solo all'utenza del purge:

```sql
GRANT DELETE ON SCHEMA::PaymentOrder TO [<UTENZA_PURGE>];
```

Verifica, connessi con quell'utenza:

```sql
SELECT HAS_PERMS_BY_NAME('PaymentOrder.[Order]', 'OBJECT', 'DELETE') AS PuoCancellare;
```

Atteso: `1`. Se resta `0`, l'utenza non è quella che il servizio usa: è il
momento di scoprirlo, non alle 2 di notte.

---

## 3. Approvazione della policy

Da eseguire **con** la persona che ha esaminato il report, non per suo
conto:

```bash
purge approve <RunId-del-dry-run> --by "<Nome Cognome>" --note "<verbale/ticket>"
```

Il comando rifiuta un run inesistente, non dry-run, non concluso, o girato
con una policy diversa da quella configurata adesso — in quest'ultimo caso
stampa le due impronte a confronto. Un rifiuto qui non si aggira cambiando
la configurazione: si capisce perché differisce.

Verifica:

```sql
SELECT PolicyHash, ApprovedBy, ApprovedOn, PolicyText, Note FROM Purge.PolicyApproval;
```

Ciò che viene approvato è **la policy**, non il singolo run: una sola
approvazione copre tutte le strategie di quella configurazione.

---

## 4. Configurazione della prima notte

Tre valori da rivedere prima del primo `--delete`, tutti in base ai numeri
di `021` (sezione *Statistiche slice* e *Durata stimata*):

- **`MaxRowsPerBatch`** — sopra il p99 dei pesi visti in `020` §3, sotto
  ~5000 per non innescare la lock escalation.
- **`WindowStart` / `WindowEnd`** — nell'ora **locale dell'host**.
  Verificare con `date` (Linux) o `Get-Date` (Windows) sull'host, non sul
  proprio portatile: in un container è spesso UTC.
- **`InterSliceDelay`** — il default (100 ms) è pensato per lasciare
  respiro all'operatività. Per la prima notte è ragionevole alzarlo a
  250–500 ms: si cancella meno, si disturba meno, e si misura l'impatto
  reale prima di accelerare.

**Non** cambiare `RetentionYears` né `AnchorMode`: l'approvazione decade.

Prova a vuoto, che non tocca nulla:

```bash
purge migrate --status     # atteso: exit 0, "0 da applicare"
purge once                 # atteso: exit 2, "Specificare la modalità"
```

---

## 5. La prima notte

### Come lanciarlo

Raccomandazione: **una strategia sola, la più semplice**, non tutte
insieme.

```bash
purge once --delete OrphanHistory > purge-$(date +%Y%m%d)-orphan.log 2>&1
echo "exit=$?"
```

Gli storici orfani sono righe già irraggiungibili dall'applicazione: è il
caso in cui un errore costa meno. Se va bene, la notte successiva
`Terminated`, poi le altre. Tutte insieme dalla quarta notte in poi.

Il DBA in reperibilità durante la finestra, almeno la prima volta.

### Cosa guardare mentre gira

Da un'altra sessione, ogni pochi minuti:

```sql
-- Avanzamento del run in corso.
SELECT b.Status, Slice = COUNT(*), Ordini = SUM(b.OrderCount),
       Righe = SUM(b.ActualDeletedRows)
FROM Purge.RunBatchProgress AS b
JOIN Purge.PurgeRun AS r ON r.RunId = b.RunId
WHERE r.DryRun = 0 AND r.CompletedOn IS NULL
GROUP BY b.Status;

-- Blocchi in corso: qui si vede se il purge sta disturbando l'operativita'.
SELECT session_id, blocking_session_id, wait_type, wait_time, last_wait_type, status
FROM sys.dm_exec_requests WHERE blocking_session_id <> 0;

-- Spazio del log.
SELECT name, CAST(FILEPROPERTY(name,'SpaceUsed')*8.0/1024 AS DECIMAL(12,1)) AS MB_Usati
FROM sys.database_files WHERE type_desc = 'LOG';
```

Nel log dell'applicazione: `PurgeSliceSplit` e `PurgeSliceStatusChanged`
sono avvisi normali; `PurgeSliceFailed` ripetuto con lo stesso `Motivo=Sql…`
su slice diverse merita attenzione.

### Come fermarlo, se serve

`Ctrl-C` (o l'arresto del job). Il motore chiude la slice in corso e
termina con **130**; la transazione aperta viene annullata. Il run resta
riprendibile: il lancio successivo riparte dal checkpoint, sullo stesso
insieme di candidati congelato.

**Non** uccidere il processo (`kill -9`, *End Task*): la transazione resta
aperta finché SQL Server non la annulla da solo.

**Non** cancellare righe da `Purge.*` per «ripulire»: sono la traccia di
cosa è stato fatto.

### Codici di uscita

| Codice | Significato | Cosa fare |
|---|---|---|
| 0 | Concluso | punto 6 |
| 1 | Una strategia è fallita | il run è `Failed`, terminale. Punto 7 |
| 2 | Configurazione o schema | non ha cancellato nulla. Correggere e rilanciare |
| 4 | Fuori finestra | non ha cancellato nulla |
| 5 | Finestra superata durante una fase lunga | **normale sui volumi grandi**: il run riprende dal checkpoint la notte dopo |
| 130 | Interrotto | riprende dal checkpoint |

Il 5 e il 130 non sono guasti. Su volumi reali le prime notti finiranno
quasi sempre con 5: dirlo prima a chi guarda i job evita una telefonata.

---

## 6. La mattina dopo

```
sqlcmd -S <host> -d <database> -E -i db/021_analisi_post_dry_run.sql -o 021-post.out
sqlcmd -S <host> -d <database> -E -i db/009_verify_audit.sql          -o 009.out
sqlcmd -S <host> -d <database> -E -i db/007_verify_collective_atomicity.sql -o 007.out
```

**Va bene se:**

- La fase è `Completed`, oppure `CompletedWithErrors` con abbandoni
  identificati (sezione H di `021`: dopo la bisezione ogni slice
  abbandonata contiene un aggregato solo, e `LastError` dice perché).
- Sezione *Previsto vs effettivo*: gli scostamenti sono zero o spiegabili.
  Uno scostamento negativo su una tabella significa che si è cancellato
  meno del previsto — di norma sono gli aggregati abbandonati.
- `009_verify_audit.sql` non segnala incongruenze fra audit e slice.
- Nessun ticket dall'operatività sulla notte.

**Da esaminare col referente applicativo:**

- Ogni riga della sezione *Abbandonati*. Un `Sql547` ricorrente sulla stessa
  FK indica un pattern nei dati, non un caso isolato.
- `StatusChangedDuringExecution` frequente: significa che ordini terminali
  da anni cambiano stato durante la notte. Vale la pena capire quale
  processo lo fa.

**Fermare tutto e non rilanciare se:**

- Un run è `Failed` con un `LastError` che non è una validazione.
- `007` segnala un collettivo spezzato fra due slice.
- L'operatività ha avuto rallentamenti o deadlock attribuibili alla
  finestra: alzare `InterSliceDelay`, abbassare `MaxRowsPerBatch`, e
  rivalutare.

---

## 7. Se un run è `Failed`

`Failed` è terminale: non riprende. Il lancio successivo ne crea uno nuovo,
che rifarà la selezione da capo.

1. Leggere `LastError` in `Purge.PurgeRun` e la sezione C di `021`
   (`ValidationFinding`).
2. Se è una validazione (V1–V5), è un'anomalia dei dati: va corretta o
   censita prima di rilanciare, altrimenti il run nuovo fallirà uguale.
3. Se è un'eccezione o un timeout, è un difetto o un guasto: non rilanciare
   alla cieca.
4. Ciò che era già stato cancellato **resta cancellato** — le slice
   committano una per una. Non c'è uno stato a metà incoerente: ogni
   aggregato è intero o assente.

---

## 8. Messa a regime

Solo dopo che almeno una notte di ogni strategia è andata a buon fine e i
numeri sono stati letti.

- Pianificare `purge once --delete` come job notturno, oppure attivare il
  servizio con cron interno. Il lock di istanza impedisce sovrapposizioni,
  ma è preferibile che lo scheduler non le tenti nemmeno.
- Allarmi utili, se avete un collettore di metriche:
  `purge.slices_abandoned` (crescita anomala), `purge.slices_split`
  (crescita regolare = i dati cambiano fra selezione ed esecuzione più
  spesso di quanto il disegno assuma), `purge.slice_duration` (p95 in
  crescita = indici o statistiche da rivedere).
- Un controllo settimanale con `021`, finché non si stabilizza.
- Riportare a 100 ms `InterSliceDelay`, un passo alla volta, misurando.
- `StagingRetentionDays` (default 7) va alzato se si vuole poter indagare
  un abbandono di due settimane prima.

---

## Riepilogo dei comandi

```bash
purge migrate --status                          # schema allineato?
purge once --dry-run                            # simulazione
purge approve <RunId> --by "<nome>" --note "…"  # approvazione della policy
purge once --delete <Strategia>                 # esecuzione reale
purge once --delete                             # tutte le strategie
purge                                           # servizio con cron interno
```

`--no-window` esiste per i recuperi e i collaudi. Con `--delete`, in
produzione, non va usato senza una ragione scritta: la finestra è ciò che
tiene il purge lontano dall'operatività.
