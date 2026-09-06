# V7 — ciclo di vita del run

Due esiti diventano quattro. Prima esistevano `Completed` e `Failed`, e `Failed`
era sia l'esito di un difetto sia quello di una rete caduta per un secondo.

Richiede `db/010_run_lifecycle.sql`. Senza, il motore non parte: `SchemaVerifier`
controlla anche le colonne.

## 1. Un guasto non chiude piu' il run

`Failed` è terminale, e `FindResumableAsync` lo esclude. Qualunque eccezione lo
produceva, quindi una disconnessione a metà notte buttava un set di candidati
congelato e tutte le slice già eseguite.

L'orchestratore distingue ora fra guasto dell'ambiente e difetto del programma.
Sul guasto la fase **non cambia** — è il checkpoint da cui riprendere, esattamente
come già faceva il ramo della cancellazione — e viene incrementato un contatore.
Sul difetto il run va in `Failed` come prima.

Nel dubbio si classifica come difetto: fermarsi e chiedere aiuto è meno grave che
riprovare all'infinito una cancellazione sbagliata.

Non è stata introdotta una fase `Interrupted`, che era l'idea di partenza:
scriverla nella colonna `Phase` avrebbe cancellato l'informazione su quale fase
fosse in corso, e la ripresa non avrebbe saputo da dove ripartire. Vedere
`docs/decisioni.md`, D-5.

## 2. Contatore delle interruzioni

Senza un limite, un guasto stabile — disco pieno, credenziale scaduta — farebbe
ripartire lo stesso run ogni notte per sempre, con lo stesso esito e senza che
nessuno se ne accorga. Oltre `Purge:MaxRunInterruptions` (5 di default) il run
viene dichiarato `Failed` con il motivo, e chiede attenzione.

La cancellazione da chiusura della finestra operativa non conta: è il
funzionamento previsto.

## 3. La connessione si apre dentro il `try`

In `SliceExecutor` l'apertura stava fuori dai `catch`. Una connessione che non si
apre è precisamente ciò che accade quando la rete ha un singhiozzo, e l'eccezione
li scavalcava tutti risalendo fino all'orchestratore. Ora è una slice da
riprovare.

Conseguenza: `conn` e `tx` non sono più `await using` ma variabili liberate in un
`finally`, e `SafeRollbackAsync` accetta una transazione nulla, perché il guasto
può avvenire prima che esista.

## 4. `SqlErrors`

I codici transitori erano tre numeri dentro `SliceExecutor`. Ora servono in due
punti — la slice, che riprova, e l'orchestratore, che decide se il run resta
riprendibile — e due liste divergenti darebbero il caso peggiore: una slice che
riprova un errore che l'orchestratore considera definitivo.

L'elenco è stato esteso agli errori di connessione persa, rifiutata o azzerata,
che sono quelli che si vedono di notte senza nessuno davanti.

## 5. `CompletedWithErrors`

Un run che abbandona delle slice lascia a database aggregati che nessuno ha
cancellato. Chiudeva in `Completed`, e la differenza viveva in una riga di log.
L'housekeeping trattava già i due casi in modo diverso, ma deducendolo da
`RunBatchProgress` invece che dallo stato del run.

Il conteggio degli abbandoni viene letto dal database, non dal contatore
dell'invocazione corrente: un run che abbandona una notte e completa quella dopo
chiuderebbe altrimenti come pulito.

## 6. Fasi terminali derivate dall'enum

L'elenco era ripetuto in quattro punti: orchestratore, `PhaseResult`, ricerca dei
run riprendibili, housekeeping. `RunPhases.IsTerminal` e `RunPhases.TerminalSqlList`
lo derivano dal tipo, così una fase aggiunta non può essere dimenticata in una
query.

`Purge.PurgeRun.Phase` passa a `VARCHAR(30)`. `CompletedWithErrors` starebbe
anche in `VARCHAR(20)`, ma di misura, e il troncamento silenzioso di una colonna
di stato è un guasto che si scopre tardi.

## Resta aperto

- Legal hold (PA-5), che va nella selezione.
- Il lock applicativo non ha heartbeat: se la sessione cade, il lock si rilascia
  e nessuno se ne accorge.
- Le metriche sono definite ma non esportate.
