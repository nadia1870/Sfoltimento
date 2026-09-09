# Documento di architettura

`Architecture-Functional-Design-Sfoltimento-2026-09-09.docx` è il disegno
funzionale e tecnico della soluzione, destinato a sviluppo, DBA e Compliance.
Aggiornato alla baseline `main` post-merge `feature/dapper-dbup`.

Il documento è generato, non modificato a mano: correggere il `.docx` e non i
sorgenti significa perdere la correzione alla revisione successiva.

## Rigenerare

```bash
cd docs/architettura/sorgenti
python3 genera-figure.py     # solo se sono cambiate le tre figure generate
node build.js                # produce il .docx nella cartella corrente
```

Serve `docx` (npm) per `build.js` e `matplotlib` per `genera-figure.py`.
Il file prodotto va rinominato con la data e sostituito a quello in
`docs/architettura/`, tenendo il precedente nella storia di git.

## Figure

| File | Origine |
|---|---|
| `fig1_architettura.png`, `fig_sequence.png`, `fig_collective.png`, `fig_housekeeping.png` | Versione precedente del documento. Non hanno sorgente in questo repository: per modificarle serve lo strumento con cui furono disegnate. |
| `fig2_pipeline.png`, `fig_bisezione.png`, `fig_sicurezza.png` | Generate da `sorgenti/genera-figure.py`. |

## Cosa aggiornare quando cambia il codice

- Una decisione nuova in `docs/decisioni.md` → riga nel capitolo 11, con la
  colonna *alternativa scartata* compilata.
- Una strategia nuova o una condizione di eleggibilità modificata → capitolo 4.
- Una fase o un esito nuovo → capitolo 5 **e** `fig2_pipeline.png`.
- Un'opzione nuova in `PurgeOptions` → appendice B.
- Uno script nuovo in `db/` → appendice A.

L'indice del `.docx` è un campo Word: alla prima apertura Word chiede di
aggiornare i campi. In LibreOffice: *Strumenti → Aggiorna → Indici*.
