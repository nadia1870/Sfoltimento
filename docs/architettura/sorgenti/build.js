const fs = require("fs");
const d = require("docx");
const {
  Document, Packer, Paragraph, TextRun, HeadingLevel, AlignmentType, PageBreak,
  Table, TableRow, TableCell, WidthType, ShadingType, BorderStyle, ImageRun,
  TableOfContents, Header, Footer, PageNumber, LevelFormat, convertInchesToTwip
} = d;

const ACCENT = "1F3864";
const MUTED = "44506B";
const HEAD_FILL = "E7ECF3";
const ALT_FILL = "F5F7FA";

// ---------------------------------------------------------------- helpers
const P = (text, opts = {}) => new Paragraph({
  spacing: { after: opts.after ?? 140, line: 276 },
  alignment: opts.align,
  indent: opts.indent,
  children: [new TextRun({
    text, italics: opts.italics, bold: opts.bold, size: opts.size ?? 21,
    color: opts.color, font: opts.font
  })]
});

// paragraph with mixed runs: rich("normale ", ["grassetto", {b:1}], "resto")
const rich = (...parts) => new Paragraph({
  spacing: { after: 140, line: 276 },
  children: parts.map(p => Array.isArray(p)
    ? new TextRun({ text: p[0], bold: p[1]?.b, italics: p[1]?.i, size: 21,
                    font: p[1]?.mono ? "Consolas" : undefined,
                    color: p[1]?.mono ? "20304A" : undefined })
    : new TextRun({ text: p, size: 21 }))
});

const H1 = (text) => new Paragraph({
  heading: HeadingLevel.HEADING_1, spacing: { before: 360, after: 160 },
  children: [new TextRun({ text, bold: true, size: 30, color: ACCENT })]
});

const H2 = (text) => new Paragraph({
  heading: HeadingLevel.HEADING_2, spacing: { before: 260, after: 120 },
  children: [new TextRun({ text, bold: true, size: 24, color: ACCENT })]
});

const H3 = (text) => new Paragraph({
  heading: HeadingLevel.HEADING_3, spacing: { before: 200, after: 100 },
  children: [new TextRun({ text, bold: true, size: 22, color: "2E4570" })]
});

const bullet = (text, level = 0) => new Paragraph({
  numbering: { reference: "punti", level },
  spacing: { after: 90, line: 276 },
  children: [new TextRun({ text, size: 21 })]
});

const check = (text) => new Paragraph({
  spacing: { after: 90 },
  children: [new TextRun({ text: "\u2610  " + text, size: 21 })]
});

const code = (lines) => lines.map((l, i) => new Paragraph({
  spacing: { after: i === lines.length - 1 ? 160 : 0, line: 240 },
  indent: { left: convertInchesToTwip(0.25) },
  shading: { type: ShadingType.CLEAR, fill: "F2F4F7" },
  children: [new TextRun({ text: l || " ", font: "Consolas", size: 18, color: "20304A" })]
}));

const note = (text) => new Paragraph({
  spacing: { before: 60, after: 180, line: 276 },
  indent: { left: convertInchesToTwip(0.25) },
  border: { left: { style: BorderStyle.SINGLE, size: 18, color: "8FA3C0", space: 12 } },
  children: [new TextRun({ text, size: 20, color: MUTED })]
});

const figure = (file, caption, widthIn = 6.4) => {
  const dim = require("child_process").execSync(
    `python3 -c "import struct;f=open('${file}','rb').read();print(struct.unpack('>II',f[16:24])[0],struct.unpack('>II',f[16:24])[1])"`
  ).toString().trim().split(" ").map(Number);
  const h = widthIn * dim[1] / dim[0];
  return [
    new Paragraph({
      alignment: AlignmentType.CENTER, spacing: { before: 160, after: 60 },
      children: [new ImageRun({
        type: "png", data: fs.readFileSync(file),
        transformation: { width: widthIn * 96, height: h * 96 }
      })]
    }),
    new Paragraph({
      alignment: AlignmentType.CENTER, spacing: { after: 240 },
      children: [new TextRun({ text: caption, italics: true, size: 18, color: MUTED })]
    })
  ];
};

const cell = (text, { b = false, fill, w, mono = false } = {}) => new TableCell({
  width: { size: w, type: WidthType.DXA },
  shading: fill ? { type: ShadingType.CLEAR, fill } : undefined,
  margins: { top: 70, bottom: 70, left: 110, right: 110 },
  children: String(text).split("\n").map(t => new Paragraph({
    spacing: { after: 0, line: 250 },
    children: [new TextRun({ text: t, bold: b, size: mono ? 17 : 19,
                             font: mono ? "Consolas" : undefined })]
  }))
});

const table = (headers, rows, widths, monoCols = []) => new Table({
  columnWidths: widths,
  width: { size: widths.reduce((a, b) => a + b, 0), type: WidthType.DXA },
  borders: {
    top: { style: BorderStyle.SINGLE, size: 4, color: "AAB6C8" },
    bottom: { style: BorderStyle.SINGLE, size: 4, color: "AAB6C8" },
    left: { style: BorderStyle.SINGLE, size: 4, color: "AAB6C8" },
    right: { style: BorderStyle.SINGLE, size: 4, color: "AAB6C8" },
    insideHorizontal: { style: BorderStyle.SINGLE, size: 2, color: "CBD4E1" },
    insideVertical: { style: BorderStyle.SINGLE, size: 2, color: "CBD4E1" }
  },
  rows: [
    new TableRow({
      tableHeader: true,
      children: headers.map((h, i) => cell(h, { b: true, fill: HEAD_FILL, w: widths[i] }))
    }),
    ...rows.map((r, ri) => new TableRow({
      children: r.map((c, i) => cell(c, {
        w: widths[i], fill: ri % 2 ? ALT_FILL : undefined, mono: monoCols.includes(i)
      }))
    }))
  ]
});

const spacer = () => new Paragraph({ spacing: { after: 200 }, children: [] });
const pageBreak = () => new Paragraph({ children: [new PageBreak()] });

// ---------------------------------------------------------------- contenuto
const children = [];
const add = (...xs) => xs.flat().forEach(x => children.push(x));

// ---- frontespizio
add(
  new Paragraph({ spacing: { before: 2200, after: 0 }, alignment: AlignmentType.CENTER,
    children: [new TextRun({ text: "Architecture & Functional Design", bold: true, size: 44, color: ACCENT })] }),
  new Paragraph({ spacing: { before: 120, after: 0 }, alignment: AlignmentType.CENTER,
    children: [new TextRun({ text: "Sfoltimento dati OSM.PaymentOrder", bold: true, size: 34, color: ACCENT })] }),
  new Paragraph({ spacing: { before: 240, after: 0 }, alignment: AlignmentType.CENTER,
    children: [new TextRun({ text: "Motore di retention per SQL Server", size: 24, color: MUTED })] }),
  new Paragraph({ spacing: { before: 60, after: 0 }, alignment: AlignmentType.CENTER,
    children: [new TextRun({ text: "Baseline: main post-merge feature/dapper-dbup — 09/09/2026", size: 20, color: MUTED, italics: true })] }),
  new Paragraph({ spacing: { before: 900, after: 0 }, alignment: AlignmentType.CENTER,
    children: [new TextRun({ text: "Documento interno — Sviluppo, DBA, Compliance", size: 19, color: MUTED })] }),
  pageBreak()
);

// ---- come leggere
add(H1("Come leggere questo documento"));
add(P("Questo documento descrive un componente che cancella in via definitiva dati contabili da un database di produzione. È scritto perché tre lettori diversi possano fidarsi della stessa descrizione: chi mantiene il codice, chi amministra il database, chi risponde della conformità normativa."));
add(P("Non presuppone conoscenza pregressa del sistema. I capitoli 1 e 2 costruiscono il vocabolario; da lì in poi ogni capitolo dà per acquisiti solo i precedenti."));
add(spacer());
add(table(
  ["Se sei…", "Leggi almeno", "Puoi saltare"],
  [
    ["Compliance / Legal / Audit",
     "Cap. 1 (il problema), 2 (glossario), 4 (strategie: cosa viene cancellato e cosa no), 9 (sicurezza e tracciabilità), 12 (punti aperti)",
     "Cap. 6, 7, 8 (dettaglio tecnico dell'esecuzione)"],
    ["Sviluppo",
     "Tutto. Il cap. 11 (decisioni) è il più denso: spiega perché la forma ovvia di ogni scelta sarebbe sbagliata",
     "—"],
    ["DBA",
     "Cap. 1, 3 (architettura), 6 (planning), 7 (esecuzione e transazioni), 8 (staging e housekeeping), 10 (operatività)",
     "Cap. 11 se serve solo far girare il sistema"],
    ["Chi deve eseguire il primo sfoltimento",
     "Cap. 10, poi i due runbook in docs/",
     "—"]
  ],
  [2100, 4700, 2600]
));
add(note("Convenzione: i nomi in carattere monospaziato sono identificatori reali del codice o del database (classi, tabelle, opzioni di configurazione). Cercandoli nel repository si trova esattamente ciò che il documento descrive."));
add(pageBreak());

// ---- indice
add(new Paragraph({ heading: HeadingLevel.HEADING_1, spacing: { after: 200 },
  children: [new TextRun({ text: "Indice", bold: true, size: 30, color: ACCENT })] }));
add(new TableOfContents("Sommario", { hyperlink: true, headingStyleRange: "1-2" }));
add(pageBreak());

// ================================================================ 1
add(H1("1. Il problema"));

add(H2("1.1 Perché serve uno sfoltimento"));
add(P("Il database di OSM.PaymentOrder conserva ogni ordine di pagamento e la sua storia. Nulla viene mai rimosso: un ordine eseguito nel 2015 occupa spazio, rallenta le scansioni e allunga i tempi di backup e ripristino esattamente come uno di ieri."));
add(P("La normativa impone di conservare i dati contabili per un periodo determinato — nella configurazione corrente cinque anni — ma non impone di conservarli per sempre. Oltre quella soglia il dato non è più un obbligo: è un costo, e in alcune letture della normativa sulla protezione dei dati personali è un rischio."));
add(P("Sfoltire significa cancellare in via definitiva ciò che è oltre soglia, in modo verificabile, senza mai lasciare il database in uno stato incoerente e senza disturbare l'operatività."));

add(H2("1.2 L'aggregato che si deve cancellare"));
add(P("Un ordine di pagamento non è una riga. È un aggregato di quattro livelli, legato da vincoli di integrità referenziale:"));
add(bullet("La testata, in PaymentOrder.Order: chi paga, quanto, in che stato."));
add(bullet("I dettagli, in una delle undici tabelle specializzate per tipo di pagamento (BankTransfer, QRBill, AccountTransfer e così via): un ordine ha un solo tipo di dettaglio."));
add(bullet("Gli storici della testata, in OrderHistory: una riga per ogni revisione. Un ordine modificato dieci volte ha dieci righe di storico."));
add(bullet("Gli storici dei dettagli, in undici tabelle parallele: ogni revisione della testata può avere la propria fotografia del dettaglio."));
add(P("Un ordine con dieci revisioni occupa quindi fino a ventuno righe distribuite su quattro tabelle diverse. Questo rapporto — non uno a uno, ma uno a molti — è il motivo per cui il volume da cancellare non si stima contando gli ordini."));

add(H2("1.3 Perché non basta un DELETE"));
add(P("La domanda naturale è perché non si scriva semplicemente:"));
add(code(["DELETE FROM PaymentOrder.[Order] WHERE ExecutionDate < '2021-01-01';"]));
add(P("Tre ragioni indipendenti, ciascuna sufficiente."));
add(rich(["Le foreign key lo impediscono. ", { b: 1 }],
  "Le tabelle figlie referenziano la testata con vincoli ", ["NO ACTION", { mono: 1 }],
  ": SQL Server rifiuta la cancellazione del padre finché esiste un figlio. Occorre cancellare in ordine inverso di dipendenza, dalle foglie alla radice."));
add(rich(["Una transazione così grande è ingestibile. ", { b: 1 }],
  "Cancellare milioni di righe in un'unica transazione fa crescere il log fino a saturarlo, innesca la lock escalation — SQL Server converte i lock di riga in un lock di tabella — e blocca l'operatività per ore. Un errore a tre quarti del lavoro annulla tutto."));
add(rich(["Non tutti gli ordini oltre soglia sono cancellabili. ", { b: 1 }],
  "Alcuni sono referenziati da un modello riutilizzabile; altri appartengono a un ordine collettivo i cui componenti non sono tutti oltre soglia; altri ancora hanno storici che puntano fuori dal proprio aggregato. Su ciascuno di questi casi la cancellazione va evitata, non forzata."));

add(H2("1.4 Cosa fa il motore, in una frase"));
add(P("Seleziona gli aggregati eleggibili secondo una regola scritta, li congela in un elenco persistente, verifica che siano davvero cancellabili, li divide in unità di lavoro piccole, e cancella ciascuna unità in una transazione breve, registrando ciò che ha cancellato — con la possibilità di interrompersi e riprendere in qualsiasi momento."));
add(note("Ogni parola di quella frase corrisponde a un capitolo: selezione (cap. 4), congelamento e ripresa (cap. 5), verifica (cap. 4.6), divisione (cap. 6), esecuzione e registrazione (cap. 7)."));
add(pageBreak());

// ================================================================ 2
add(H1("2. Glossario"));
add(P("Dieci termini ricorrono in tutto il documento e nel codice. Sono definiti qui una volta sola."));
add(spacer());
add(table(
  ["Termine", "Significato"],
  [
    ["Run", "Una singola esecuzione del motore per una strategia. Ha un identificatore, una fase, un insieme di candidati congelato e una traccia in Purge.PurgeRun. Un run può durare più notti."],
    ["Fase", "Lo stato del run nel suo ciclo di vita: Selecting, Expanding, Validating, Planning, Executing, più gli stati terminali. La fase è anche il checkpoint da cui si riprende."],
    ["Strategia", "La regola che decide quali aggregati sono eleggibili. Cinque strategie indipendenti, descritte nel cap. 4."],
    ["Candidato", "Un aggregato selezionato da una strategia e registrato nello staging. Essere candidato non significa essere cancellato: la validazione e l'esecuzione possono escluderlo."],
    ["Aggregato", "L'unità indivisibile di cancellazione: un ordine con tutta la sua storia, oppure un ordine collettivo con tutti i suoi componenti."],
    ["Slice (o batch)", "Un insieme di aggregati cancellati in una sola transazione. Identificata da BatchNo all'interno del run."],
    ["Peso (RowWeight)", "Righe stimate per un aggregato: 1 + 2 × numero di revisioni. È la misura su cui si dimensionano le slice, non il numero di ordini."],
    ["Filigrana (anchor)", "La coppia (data, id) dell'ultima riga letta, usata per riprendere la lettura paginata senza rileggere né saltare righe."],
    ["Staging", "Le tabelle di lavoro dello schema Purge che contengono i candidati del run: RunCandidateOrder, RunCandidateOrderHistory, RunCandidateCollective."],
    ["Policy", "La combinazione di parametri che determina cosa viene cancellato: anni di retention, modo di ancoraggio, strategie attive. Ha un'impronta crittografica ed è ciò che viene approvato."]
  ],
  [2200, 7200]
));
add(pageBreak());

// ================================================================ 3
add(H1("3. Architettura"));

add(H2("3.1 Quadro d'insieme"));
add(P("Il motore è un'applicazione .NET 10 autonoma, che parla con SQL Server tramite SQL scritto a mano e non dipende dal DbContext dell'applicazione principale. Questa indipendenza è deliberata: il purge deve poter evolvere senza toccare l'applicazione, e deve poter girare con un'utenza e con permessi propri."));
add(figure("../figure/fig1_architettura.png", "Figura 1 — Architettura logica: dal workflow al confine SQL"));
add(P("La catena di responsabilità va letta dall'alto verso il basso; ogni livello sa solo ciò che gli serve."));
add(spacer());
add(table(
  ["Componente", "Responsabilità", "Cosa NON fa"],
  [
    ["RetentionCronService / Host", "Decide quando eseguire e con quale modalità; traduce l'esito in un codice di uscita", "Non conosce le regole di eleggibilità"],
    ["RetentionOrchestrator", "Governa il workflow: esegue una fase, persiste la transizione, gestisce interruzioni e ripresa", "Non esegue SQL di dominio"],
    ["IPurgeStrategy", "Definisce l'eleggibilità: quali aggregati entrano nel run", "Non decide come e quando cancellarli"],
    ["BatchPlanner", "Legge i candidati a pagine e li dà in pasto al packer", "Non decide la composizione delle slice"],
    ["BatchPacker", "Assegna i candidati alle slice rispettando i limiti", "Non accede al database: è una funzione pura"],
    ["BatchExecutionCoordinator", "Politica di esecuzione: finestra oraria, ritentativi, bisezione, abbandono, ritmo", "Non conosce la persistenza né il SQL"],
    ["SliceExecutor", "Il confine transazionale: una slice, una transazione", "Non decide cosa fare in caso di errore: classifica e riporta"],
    ["PurgeHousekeeping", "Sfoltisce lo staging del purge stesso", "Non tocca i dati di dominio né l'audit"]
  ],
  [2500, 4400, 2500]
));
add(note("La separazione fra coordinatore e persistenza non è teorica: un test verifica per riflessione che BatchExecutionCoordinator non abbia alcun riferimento a PurgeRunStore. È il punto in cui, in futuro, si potrebbe innestare un'esecuzione distribuita su più nodi."));

add(H2("3.2 Il flusso completo"));
add(figure("../figure/fig_sequence.png", "Figura 2 — Sequenza di un run di retention, dall'avvio all'audit"));
add(pageBreak());

// ================================================================ 4
add(H1("4. Le strategie di selezione"));
add(P("La strategia è il cuore funzionale del sistema: è la traduzione in codice della regola di conservazione. Ogni strategia è indipendente, produce un run separato con un proprio identificatore, e può essere attivata o disattivata in configurazione."));
add(P("Tutte condividono tre proprietà. La lettura è paginata per non tenere lock lunghi sulle tabelle applicative. La selezione è idempotente: rieseguirla su un run interrotto non duplica i candidati. E il risultato è congelato: una volta selezionati, i candidati non cambiano più, anche se il run prosegue per più notti."));

add(H2("4.1 La soglia di retention"));
add(P("Prima delle strategie viene la soglia, calcolata una volta per run e registrata su di esso. Due modi di ancoraggio:"));
add(spacer());
add(table(
  ["AnchorMode", "Soglia con RetentionYears = 5, oggi 09/09/2026", "Significato"],
  [
    ["FiscalYearEnd", "01/01/2021", "Si conserva l'esercizio corrente più i cinque precedenti per intero. La soglia si sposta una volta l'anno, il 1° gennaio."],
    ["RollingDate", "09/09/2021", "Cinque anni esatti dalla data odierna. La soglia si sposta ogni giorno."]
  ],
  [1900, 3200, 4300]
));
add(note("La scelta fra i due non è tecnica: dipende da come si legge l'obbligo di conservazione (punto aperto PA-3). FiscalYearEnd è più conservativo — conserva in media sei mesi in più — ed è il default."));

add(H2("4.2 Terminated — ordini conclusi"));
add(P("È la strategia principale, quella che muove i volumi. Seleziona gli ordini singoli il cui ciclo di vita è concluso."));
add(spacer());
add(table(
  ["Condizione", "Motivo"],
  [
    ["StatusCode fra Executed, Cancelled, Deleted, Refused, Extincted", "Sono gli stati terminali: l'ordine non evolverà più. Un ordine in lavorazione non si cancella, quale che sia la sua data."],
    ["StandingOrder = 0", "I piani ricorrenti hanno una soglia diversa: li tratta la strategia StandingOrders."],
    ["ExecutionDate < soglia", "Il criterio di anzianità."],
    ["ExecutionDate >= 1900-01-01", "Esclude le date sentinella, valori convenzionali che non rappresentano una data reale e che renderebbero eleggibile un ordine per errore."],
    ["Nessun record in Model che punti all'ordine", "L'ordine è il modello di un pagamento ricorrente riutilizzabile: cancellarlo distruggerebbe il modello."],
    ["Nessuna appartenenza a un ordine collettivo", "Se appartiene a un collettivo, va cancellato con l'intero collettivo o non va cancellato affatto."]
  ],
  [3400, 6000]
));

add(H2("4.3 StandingOrders — piani ricorrenti"));
add(P("Un piano ricorrente ha una data di esecuzione iniziale ma continua a produrre pagamenti nel tempo. Misurarne l'anzianità sulla data di partenza sarebbe sbagliato: un piano avviato nel 2019 e ancora attivo verrebbe cancellato mentre è in uso."));
add(rich("La soglia si applica quindi a ", ["StandingOrder.LastExecutionDate", { mono: 1 }],
  ", la data dell'ultimo pagamento prodotto. Restano le stesse esclusioni di Terminated: stato terminale, nessun modello, nessuna appartenenza collettiva."));
add(note("Un piano senza LastExecutionDate — mai eseguito, oppure senza scadenza — non è mai eleggibile. È una scelta prudente: senza quella data non esiste un criterio di anzianità difendibile."));

add(H2("4.4 Collective — ordini collettivi"));
add(P("Un ordine collettivo è una disposizione unica che raggruppa più pagamenti: uno stipendio pagato a duecento dipendenti è un collettivo con duecento componenti. La testata collettiva ha valore contabile proprio perché lega insieme i componenti."));
add(figure("../figure/fig_collective.png", "Figura 3 — Atomicità del collettivo e topologia di cancellazione"));
add(rich(["Il collettivo è indivisibile. ", { b: 1 }],
  "Se si cancellassero i componenti oltre soglia lasciando la testata, resterebbe una disposizione contabile che punta al nulla; se si cancellasse la testata lasciando i componenti, resterebbero pagamenti orfani della loro giustificazione. Il collettivo è quindi eleggibile solo se ",
  ["tutti", { i: 1 }], " i suoi componenti lo sono, e viene cancellato per intero nella stessa transazione."));
add(P("Un collettivo è eleggibile quando: ha una data di esecuzione valorizzata e oltre soglia; il suo stato è fra Executed, Cancelled, Refused, PartiallyExecuted; e ogni componente è in stato terminale e oltre soglia."));

add(H3("Le esclusioni censite"));
add(P("Alcuni collettivi superano il vaglio di eleggibilità ma contengono un'anomalia che ne impedisce la cancellazione. Fino alla revisione D-10 queste anomalie emergevano in validazione e facevano fallire l'intero run: un solo collettivo anomalo bloccava stabilmente tutta la strategia, notte dopo notte."));
add(rich("Oggi il collettivo viene ", ["escluso e censito", { b: 1 }], " in ", ["Purge.RunCandidateCollective", { mono: 1 }],
  " con un motivo, e il run prosegue sugli altri. Ciò che viene escluso resta a database finché qualcuno lo esamina."));
add(spacer());
add(table(
  ["Motivo registrato", "Che cosa significa"],
  [
    ["ComponentHasModel", "Un componente è il modello di un pagamento ricorrente."],
    ["AmbiguousMembership", "Un ordine risulta appartenere a due collettivi eleggibili: non è decidibile quale dei due lo possieda. Vengono esclusi entrambi."],
    ["CrossRef:<tabella>", "Uno storico di dettaglio di un componente punta al dettaglio di un ordine esterno al collettivo."],
    ["ExecutionDateNull", "Il collettivo non ha data di esecuzione: nessun criterio di anzianità applicabile (punto aperto PA-7)."]
  ],
  [2900, 6500]
));

add(H2("4.5 OrphanHistory — storici orfani"));
add(rich("Righe di ", ["OrderHistory", { mono: 1 }], " il cui riferimento alla testata è nullo: la testata non esiste più, o non è mai esistita. Sono dati irraggiungibili dall'applicazione. La soglia si applica alla data di ultimo aggiornamento. È la strategia più semplice e quella con cui conviene cominciare la prima esecuzione reale."));

add(H2("4.6 Abandoned — ordini mai completati (non attiva)"));
add(rich("Ordini rimasti negli stati ", ["Created", { mono: 1 }], " o ", ["PartiallyAuthorised", { mono: 1 }],
  " oltre una soglia in mesi, misurata sulla data di creazione: disposizioni iniziate e mai portate a termine. La strategia è implementata ma ",
  ["disattivata", { b: 1 }], " (", ["AbandonedEnabled = false", { mono: 1 }],
  "), in attesa che il business confermi la soglia e che venga chiarito se un ordine mai autorizzato abbia rilevanza contabile (punto aperto PA-21)."));

add(H2("4.7 Espansione e validazione"));
add(P("Selezionati i candidati, il motore ne espande gli storici — registrandone anche il peso — e verifica cinque invarianti prima di cancellare qualsiasi cosa."));
add(spacer());
add(table(
  ["Regola", "Verifica", "Se fallisce"],
  [
    ["V1", "Nessuno storico di dettaglio candidato punta a un dettaglio fuori dall'insieme", "Il run fallisce: è un difetto della selezione"],
    ["V2", "Nessun candidato è referenziato da un modello", "Il run fallisce"],
    ["V3", "Ogni componente collettivo candidato appartiene a un collettivo candidato", "Il run fallisce"],
    ["V4", "Nessun candidato ha cambiato stato dopo la selezione", "Il run fallisce"],
    ["V5", "Ogni storico degli ordini candidati è stato espanso", "Il run fallisce"]
  ],
  [900, 5900, 2600]
));
add(note("Le validazioni sono deliberatamente intransigenti: dopo le esclusioni del paragrafo 4.4, un riscontro qui non è un dato strano ma un difetto del programma, e deve fermare tutto. Il censimento delle anomalie note avviene prima, in selezione; la validazione è la rete di sicurezza, e una rete che scarta in silenzio non è più una rete."));
add(pageBreak());

// ================================================================ 5
add(H1("5. Il ciclo di vita di un run"));
add(P("Un run attraversa una sequenza fissa di fasi. La regola che governa tutto è una sola: si esegue una fase, si persiste la transizione, si aggiorna il modello. Mai il contrario."));
add(figure("../figure/fig2_pipeline.png", "Figura 4 — Fasi, esiti terminali e ripresa"));

add(H2("5.1 La fase è il checkpoint"));
add(P("Non esiste una tabella di avanzamento separata: la fase corrente registrata sul run è il punto di ripresa. Se il processo cade durante l'espansione, la fase resta Expanding, e il lancio successivo riprende da lì. Questo vincolo ha una conseguenza precisa: ogni fase deve essere rieseguibile senza danno, e infatti tutte le operazioni di selezione ed espansione sono idempotenti."));

add(H2("5.2 Interruzione e ripresa"));
add(rich("Un guasto passeggero — la rete che cade, un timeout, la finestra oraria che si chiude — ",
  ["non", { i: 1 }], " fa avanzare la fase. Incrementa un contatore di interruzioni sul run e termina. La notte successiva il run riprende esattamente da dove si era fermato, sullo stesso insieme di candidati congelato: una ripresa dopo tre notti opera sui candidati selezionati la prima notte, non su un insieme ricalcolato."));
add(rich("Oltre ", ["MaxRunInterruptions", { mono: 1 }], " interruzioni (cinque per default) il run viene dichiarato ",
  ["Failed", { mono: 1 }], ". È il modo di distinguere un guasto occasionale da un problema stabile che nessuno sta guardando."));

add(H2("5.3 Gli esiti"));
add(spacer());
add(table(
  ["Esito", "Significato", "Riprende?"],
  [
    ["Completed", "Tutti gli aggregati candidati sono stati cancellati", "No: concluso"],
    ["CompletedWithErrors", "Concluso, ma alcune slice sono state abbandonate: richiedono analisi", "No: concluso"],
    ["Failed", "Validazione fallita, difetto, o troppe interruzioni", "No: il lancio successivo crea un run nuovo"],
    ["Aborted", "Annullato deliberatamente", "No"],
    ["(fase non terminale)", "Interrotto: guasto, finestra chiusa, arresto richiesto", "Sì, dal checkpoint"]
  ],
  [2300, 5200, 1900]
));
add(pageBreak());

// ================================================================ 6
add(H1("6. Planning: dai candidati alle slice"));
add(P("Cancellare tutti i candidati in una volta è impossibile; cancellarli uno per uno sarebbe lentissimo. Il planning cerca la dimensione intermedia: unità abbastanza grandi da essere efficienti, abbastanza piccole da non bloccare il database."));

add(H2("6.1 Il peso, non il conteggio"));
add(rich("La dimensione di una slice si misura in righe stimate, non in ordini. Il peso di un aggregato è ",
  ["1 + 2 × numero di revisioni", { b: 1 }], ": la testata, più una riga di storico e una di storico di dettaglio per revisione. Un ordine senza revisioni pesa 1; uno con cinquanta revisioni pesa 101."));
add(P("Due limiti, entrambi rispettati: un tetto di righe per slice (3000 per default) e un tetto di aggregati per slice (500). Il primo tiene la transazione sotto la soglia oltre la quale SQL Server converte i lock di riga in un lock di tabella; il secondo evita transazioni con troppi oggetti anche quando le righe sono poche."));

add(H2("6.2 Le regole di composizione"));
add(bullet("Ordini singoli: si accumulano fino al raggiungimento di uno dei due limiti."));
add(bullet("Collettivi: tutti i componenti dello stesso collettivo finiscono nella stessa slice. Non è negoziabile, ed è ciò che rende la cancellazione atomica."));
add(bullet("Aggregati sovradimensionati: un aggregato il cui peso individuale supera il tetto — tipicamente un collettivo grande, o un ordine con centinaia di revisioni — diventa una slice a sé. Si accetta consapevolmente un picco di lock, perché l'alternativa sarebbe spezzare l'aggregato."));

add(H2("6.3 Il packer è una funzione pura"));
add(P("La composizione delle slice non accede al database, non apre connessioni e non decide l'eleggibilità: riceve una sequenza di candidati con il loro peso e restituisce assegnazioni. Questo la rende verificabile in modo esaustivo con test in memoria, senza database — ed è il motivo per cui i casi limite (aggregato sovradimensionato, collettivo a cavallo di due slice, ordine senza peso) sono coperti da test rapidi e deterministici."));
add(note("La lettura dei candidati usa paginazione per chiave (keyset) invece che per offset: si ricorda l'ultima coppia (data, id) letta e si riprende da lì. Con OFFSET la pagina numero mille richiederebbe a SQL Server di scorrere e scartare le novecentonovantanove precedenti."));
add(pageBreak());

// ================================================================ 7
add(H1("7. Esecuzione: la cancellazione vera"));

add(H2("7.1 Una slice, una transazione"));
add(P("Ogni slice è cancellata in una transazione esplicita, in isolamento READ COMMITTED, con priorità di deadlock bassa: se il purge e l'operatività si contendono le stesse righe, è il purge a cedere."));
add(P("All'interno della transazione l'ordine di cancellazione segue la topologia delle dipendenze, dalle foglie alla radice:"));
add(code([
  "1.  storici di dettaglio       (undici tabelle)",
  "2.  OrderHistory               (storici della testata)",
  "3.  dettagli correnti + Model  (undici tabelle)",
  "4.  Order                      (la testata)",
  "    [+ per i collettivi: le tabelle di raggruppamento e la testata collettiva]"
]));
add(rich(["Audit e checkpoint vengono scritti nella stessa transazione delle cancellazioni. ", { b: 1 }],
  "Non è un dettaglio implementativo: significa che il progresso registrato non può divergere da ciò che è stato realmente cancellato. Se la transazione fallisce, spariscono insieme le cancellazioni e la loro registrazione; se riesce, esistono entrambe."));

add(H2("7.2 La rivalidazione in transazione"));
add(P("Prima di dichiarare conclusa la slice, il motore confronta il numero di testate effettivamente cancellate con quello atteso. Se non coincide, significa che qualcuno ha modificato un ordine fra la selezione e adesso — l'ordine è tornato in lavorazione, o è stato cancellato da altri — e la transazione viene annullata per intero. Procedere lascerebbe a database una testata senza storici e senza dettagli, che è peggio del non fare nulla."));

add(H2("7.3 Quando qualcosa va storto"));
add(P("Gli errori vengono classificati in quattro categorie, e ciascuna ha una risposta diversa. Questa è la parte più delicata del sistema e quella dove le scelte sbagliate sono meno visibili."));
add(spacer());
add(table(
  ["Categoria", "Esempi", "Risposta"],
  [
    ["Contesa", "Deadlock, timeout di lock, timeout di comando", "Si riprova la stessa slice, fino a tre volte. È il caso in cui riprovare può funzionare."],
    ["Integrità dei dati", "Foreign key violata, chiave duplicata", "Non si riprova: si divide la slice (§7.4). Riprovare confermerebbe solo lo stesso rifiuto."],
    ["Stato cambiato in corsa", "Il conteggio delle testate non torna", "Come sopra: si divide. Il conteggio riflette uno stato già committato da altri."],
    ["Guasto di connessione", "La rete cade, il server si riavvia", "Il run resta riprendibile: si esce e si riprende dal checkpoint."],
    ["Tutto il resto", "Un difetto del programma, un permesso mancante", "La slice viene abbandonata: dividere un difetto lo moltiplica soltanto."]
  ],
  [2100, 3300, 4000]
));

add(H2("7.4 La bisezione: isolare il colpevole"));
add(P("Il caso tipico è un solo ordine problematico dentro una slice da cinquecento. Abbandonare l'intera slice significherebbe perdere quattrocentonovantanove cancellazioni legittime per colpa di una."));
add(figure("../figure/fig_bisezione.png", "Figura 5 — La slice si divide finché l'abbandono riguarda un aggregato solo"));
add(P("La slice viene divisa in due per aggregato; le metà sane vengono cancellate e non più toccate, la metà problematica si divide ancora, finché la slice che fallisce contiene un aggregato solo — quello, e solo quello, viene abbandonato. Il costo è logaritmico: isolare un colpevole fra cinquecento richiede una ventina di transazioni, non cinquecento."));
add(P("Un collettivo non si divide mai: se è lui il problema, viene abbandonato intero. È il prezzo dell'atomicità, ed è accettato consapevolmente."));
add(rich("Ogni slice figlia conserva il riferimento alla slice madre. La domanda «quale aggregato ha fatto fallire la slice 37» ha quindi una risposta diretta, e la slice trovata contiene un aggregato solo, con la causa registrata in ",
  ["LastError", { mono: 1 }], "."));

add(H2("7.5 La finestra operativa"));
add(P("Il purge gira in una finestra notturna configurabile (per default dall'una alle cinque). La finestra è rispettata a due livelli: fra una slice e l'altra si verifica se c'è ancora tempo, e le fasi lunghe come la selezione ricevono un segnale di chiusura con una tolleranza. Un run che supera la finestra non è un errore: esce con un codice dedicato, così lo scheduler lo registra, e riprende la notte dopo."));
add(note("Sui volumi di produzione le prime notti finiranno quasi sempre così. È il comportamento previsto, non un guasto — vale la pena dirlo in anticipo a chi sorveglia i job."));
add(pageBreak());

// ================================================================ 8
add(H1("8. Lo staging e la sua pulizia"));
add(P("Il motore produce dati di lavoro in proporzione a ciò che cancella: una riga per ogni ordine candidato e una per ogni revisione. Su volumi reali questo staging può superare in dimensione i dati che ha rimosso. Il componente di sfoltimento deve quindi sfoltire anche se stesso."));
add(figure("../figure/fig_housekeeping.png", "Figura 6 — Ciclo di vita dello staging e pulizia"));
add(spacer());
add(table(
  ["Tabella", "Contenuto", "Sfoltita?"],
  [
    ["RunCandidateOrder\nRunCandidateOrderHistory\nRunCandidateCollective", "Lo staging: i candidati del run e i loro storici", "Sì. Sette giorni per i run conclusi senza incidenti, novanta per quelli falliti o con abbandoni — lì lo staging è l'unico appiglio per l'analisi"],
    ["PurgeRun", "Una riga per run: fase, policy, tempi", "No"],
    ["RunBatchProgress", "Una riga per slice: stato, tentativi, genealogia delle bisezioni", "No"],
    ["PurgeAudit", "Ciò che è stato realmente cancellato, per run/slice/tabella", "No: è l'audit trail"],
    ["DryRunReport", "Le previsioni delle simulazioni", "No"],
    ["ValidationFinding", "Le anomalie rilevate dalle validazioni", "No"],
    ["PolicyApproval", "Chi ha approvato quale policy, quando", "No"],
    ["SchemaVersions", "Le migrazioni applicate al database", "No"]
  ],
  [2600, 4200, 2600]
));
add(P("La pulizia avviene a batch, rispetta la stessa finestra oraria del resto, e marca il run come ripulito solo dopo aver svuotato tutte e tre le tabelle. Non lascia traccia nell'audit: registrare la pulizia dello staging genererebbe a sua volta righe da ripulire."));
add(note("Le tabelle mai sfoltite sono piccole per costruzione — una riga per run, per slice, per tabella toccata — ma nessuna ha oggi un limite superiore. Se dopo i primi mesi la loro crescita risultasse significativa, andrà definita una retention anche per l'audit: è una decisione di conformità, non tecnica."));
add(pageBreak());

// ================================================================ 9
add(H1("9. Sicurezza, conformità e tracciabilità"));
add(P("Questo capitolo risponde alla domanda che un auditor pone per prima: come si dimostra che è stato cancellato solo ciò che doveva esserlo, e che qualcuno lo aveva autorizzato."));

add(H2("9.1 I cinque livelli"));
add(figure("../figure/fig_sicurezza.png", "Figura 7 — Livelli di sicurezza indipendenti"));
add(P("Ciascun livello ferma la cancellazione da solo, e sono indipendenti fra loro: perché un dato venga cancellato devono consentirlo tutti e cinque."));
add(bullet("All'avvio, il motore verifica che lo schema di controllo sia quello atteso. Se manca una colonna, il processo non parte."));
add(bullet("La modalità — simulazione o cancellazione — si indica obbligatoriamente da riga di comando. Non è configurabile: nessun file di configurazione modificato per errore può trasformare una simulazione in una cancellazione."));
add(bullet("In simulazione il motore non percorre i rami distruttivi, e l'utenza usata per il dry-run non ha il permesso di cancellare: l'assenza di cancellazioni è una proprietà dei permessi, non una promessa del codice."));
add(bullet("La policy — anni di retention, ancoraggio, strategie attive — ha un'impronta crittografica. Prima di ogni esecuzione reale il motore verifica che quell'impronta sia stata approvata e registrata sul database. Cambiare un parametro invalida l'approvazione, e il motore se ne accorge."));
add(bullet("Le foreign key non vengono mai disabilitate. Se una cancellazione lasciasse un riferimento pendente, il database la rifiuta: è la rete finale, indipendente da ogni logica applicativa."));

add(H2("9.2 L'approvazione della policy"));
add(rich(["L'approvazione è una sola, per policy e per database: non serve approvare ogni esecuzione. ", { b: 1 }],
  "Approvata una volta, il job notturno gira indefinitamente senza intervento umano. Serve una nuova approvazione solo quando cambia ciò che verrebbe cancellato."));

add(H3("Che cosa si approva"));
add(P("Non un run, ma la regola. La policy è l'insieme dei parametri che determinano quali aggregati sono eleggibili, ridotti a un'impronta crittografica stabile:"));
add(spacer());
add(table(
  ["Dentro l'impronta (fa decadere l'approvazione)", "Fuori dall'impronta (non la tocca)"],
  [
    ["RetentionYears — anni di conservazione\nAnchorMode — ancoraggio all'esercizio o alla data\nStrategies — quali strategie sono attive\nAbandonedEnabled — se gli abbandoni si cancellano\nAbandonedRetentionMonths — la loro soglia",
     "MaxRowsPerBatch, MaxOrdersPerBatch — dimensione delle slice\nSelectionBatchSize — righe lette per pagina\nInterSliceDelay, RetryDelay — ritmo e ritentativi\nWindowStart, WindowEnd — finestra oraria\nMaxSplitDepth, MaxSliceAttempts — resilienza\nStagingRetentionDays — pulizia dello staging"]
  ],
  [4700, 4700]
));
add(P("La distinzione è deliberata e vale la pena capirla. I parametri di sinistra decidono cosa viene cancellato; quelli di destra decidono quanto lavoro si fa per volta. Se la dimensione delle slice facesse parte dell'impronta, tararla dopo il collaudo — cosa che va fatta di sicuro — invaliderebbe l'approvazione e bloccherebbe il job notturno per una modifica innocua. È così che un controllo di sicurezza perde credibilità e finisce disattivato."));

add(H3("Come si ottiene"));
add(P("Il percorso ha tre attori e non è aggirabile: chi esegue produce la simulazione, chi risponde della conformità la esamina, il DBA concede il permesso tecnico."));
add(code([
  "1.  purge once --dry-run                     simula e produce il report",
  "        -> annotare il RunId stampato all'avvio",
  "",
  "2.  [ esame del report ]                     con Compliance / referente applicativo",
  "",
  "3.  purge approve <RunId> --by \"M. Rossi\" --note \"CR-1487\"",
  "        -> registra l'approvazione della policy su questo database",
  "",
  "4.  GRANT DELETE ON SCHEMA::PaymentOrder ... il DBA, solo ora",
  "",
  "5.  purge once --delete                      esecuzione reale"
]));
add(P("Il comando di approvazione rifiuta, con un messaggio che dice quale delle quattro condizioni non è soddisfatta:"));
add(bullet("un identificativo di run inesistente su questo database;"));
add(bullet("un run che non è una simulazione — non si approva una cancellazione già avvenuta;"));
add(bullet("una simulazione non conclusa: non ha prodotto un report completo;"));
add(bullet("una simulazione girata con una policy diversa da quella configurata adesso. In questo caso stampa le due impronte a confronto: approvarla autorizzerebbe una cancellazione che nessuno ha esaminato."));
add(rich("Il nome di chi approva è obbligatorio: un'approvazione senza un nome non è un'approvazione. Il campo ",
  ["--note", { mono: 1 }], " è facoltativo ma andrebbe sempre valorizzato con il riferimento al documento che autorizza — verbale, ticket, richiesta di modifica: è ciò che collega la riga sul database alla decisione presa fuori."));

add(H3("Che cosa viene registrato"));
add(P("Una riga in Purge.PolicyApproval, con chiave l'impronta della policy: impronta, identificativo del dry-run di riferimento, data e ora, nome di chi approva, descrizione leggibile della policy e la nota. La descrizione è in chiaro proprio perché chi legge l'audit non deve interpretare un'impronta esadecimale:"));
add(code([
  "Retention=5 anni, Ancoraggio=FiscalYearEnd, Abbandonati=disattivi,",
  "Strategie=[Terminated, StandingOrders, Collective, OrphanHistory]"
]));

add(H3("Come viene verificata, a ogni esecuzione"));
add(P("Prima di ogni esecuzione reale — non solo la prima — il motore ricalcola l'impronta della configurazione corrente e cerca una riga di approvazione che le corrisponda. Se non la trova, rifiuta di partire, registra il motivo nel log ed esce con un codice diverso da zero: un purge che non parte in silenzio sembrerebbe riuscito, ed è il modo peggiore di fallire."));
add(P("Il controllo sta nei punti di ingresso — riga di comando e scheduler — e non nell'orchestratore. Così i test di integrazione non hanno bisogno di aggirarlo: un aggiramento nei test è il primo passo verso un aggiramento in produzione."));
add(note("La simulazione non richiede approvazione: non cancella nulla, e chiederla renderebbe impossibile produrre il report da approvare."));

add(H3("Quando l'approvazione decade"));
add(spacer());
add(table(
  ["Evento", "Serve riapprovare?", "Perché"],
  [
    ["Passa una notte, un mese, un anno", "No", "La policy non è cambiata: il job gira senza intervento."],
    ["La soglia si sposta al 1° gennaio", "No", "È cambiato il calendario, non la regola. Con ancoraggio all'esercizio, un anno in più diventa eleggibile: è il comportamento approvato."],
    ["Si tara la dimensione delle slice o la finestra oraria", "No", "Parametri operativi, fuori dall'impronta."],
    ["Da 5 a 7 anni di retention", "Sì", "Cambia il perimetro di ciò che si conserva."],
    ["Da FiscalYearEnd a RollingDate", "Sì", "Cambia il criterio di anzianità."],
    ["Si attiva una strategia (per esempio Abandoned)", "Sì", "Entra in gioco una categoria di dati che nessuno ha esaminato."],
    ["Si disattiva una strategia", "Sì", "L'impronta comprende l'elenco: cambia comunque, ed è corretto che qualcuno lo veda."],
    ["Si aggiorna il software senza toccare la policy", "No", "L'approvazione vive nel database, non nel codice."],
    ["Si esegue su un altro database (collaudo, nuovo ambiente)", "Sì", "L'approvazione non viaggia con il codice: là la tabella è vuota e va esaminato un dry-run prodotto su quei dati."],
    ["Si ripristina il database da un backup precedente all'approvazione", "Sì", "La riga di approvazione è stata ripristinata via con il resto."]
  ],
  [3600, 1500, 4300]
));
add(P("Una policy approvata in passato e poi abbandonata resta registrata: tornare alla configurazione precedente riattiva l'approvazione originale, con il nome e la data di allora. È coerente — quella regola era stata esaminata — ma va saputo, perché significa che la riga più recente in tabella non è necessariamente quella in vigore. La policy in vigore è quella che corrisponde alla configurazione corrente."));

add(H2("9.3 Che cosa si può dimostrare, a posteriori"));
add(spacer());
add(table(
  ["Domanda", "Dove si trova la risposta"],
  [
    ["Che cosa è stato cancellato, e quando?", "Purge.PurgeAudit: una riga per run, slice e tabella, con il conteggio delle righe realmente committate."],
    ["Con quale regola?", "Purge.PurgeRun: soglia calcolata, modo di ancoraggio, impronta della policy, tempi del run."],
    ["Chi lo aveva autorizzato?", "Purge.PolicyApproval: impronta, nome, data, riferimento al documento di approvazione."],
    ["Che cosa era stato previsto?", "Purge.DryRunReport, confrontabile con l'audit tramite una vista dedicata."],
    ["Che cosa NON è stato cancellato, e perché?", "Purge.RunCandidateCollective per i collettivi esclusi con il motivo; Purge.RunBatchProgress per le slice abbandonate; Purge.ValidationFinding per le anomalie."],
    ["Si è cancellato per sbaglio qualcosa di parziale?", "No per costruzione: ogni aggregato è cancellato per intero in una transazione, o non è cancellato affatto."]
  ],
  [3200, 6200]
));
add(note("L'audit registra ciò che è stato committato, non ciò che è stato tentato. Una slice annullata non lascia traccia nell'audit — lascia traccia nel log e nello stato della slice. Questa distinzione è deliberata: l'audit deve poter essere letto come «questo è ciò che non esiste più»."));
add(pageBreak());

// ================================================================ 10
add(H1("10. Schema, migrazioni e operatività"));

add(H2("10.1 Lo schema di controllo"));
add(rich("Tutte le tabelle del motore vivono in uno schema separato, ", ["Purge", { mono: 1 }],
  ", distinto dallo schema applicativo. Gli script di creazione sono file SQL leggibili e rivedibili dal DBA. Il comando ",
  ["purge migrate", { mono: 1 }], " li applica in ordine, uno per transazione, registrando in ",
  ["Purge.SchemaVersions", { mono: 1 }], " quelli già passati; ", ["purge migrate --status", { mono: 1 }],
  " dice cosa manca senza applicare nulla."));
add(note("Gli indici sulle tabelle applicative restano esclusi dalle migrazioni automatiche: operano su tabelle grandi e in uso, e vanno applicati dal DBA in finestra di manutenzione. Senza quegli indici la selezione scandisce l'intera tabella degli ordini a ogni pagina."));

add(H2("10.2 Come si avvia l'applicazione"));
add(P("Il motore è un eseguibile a riga di comando. In produzione si usa la versione pubblicata; in sviluppo si può lanciare dal repository."));
add(code([
  "# produzione (artefatto pubblicato)",
  "dotnet publish -c Release src/OSM.PaymentOrder.Purge.Host -o /opt/purge",
  "/opt/purge/OSM.PaymentOrder.Purge.Host once --dry-run",
  "",
  "# sviluppo (dal repository)",
  "dotnet run --project src/OSM.PaymentOrder.Purge.Host -- once --dry-run"
]));
add(rich("Negli esempi che seguono ", ["purge", { mono: 1 }],
  " sta per l'eseguibile pubblicato, o per ", ["dotnet run --project ... --", { mono: 1 }],
  ": tutto ciò che segue è identico nei due casi."));
add(note("Il processo legge appsettings.json dalla cartella dell'eseguibile, non dalla cartella corrente. Un lancio da un'altra directory funziona; un eseguibile copiato altrove senza il suo appsettings no."));

add(H3("Configurazione"));
add(P("I parametri arrivano da tre fonti, in ordine di precedenza crescente: appsettings.json accanto all'eseguibile, appsettings.<Ambiente>.json, variabili d'ambiente. In produzione la stringa di connessione va nelle variabili, non nel file."));
add(P("Le variabili usano il prefisso PURGE_ e il doppio underscore come separatore di livello:"));
add(code([
  "PURGE_ConnectionStrings__PaymentOrder=Server=host,1433;Database=...;User Id=...;Password=...;Encrypt=true",
  "PURGE_Purge__RetentionYears=5",
  "PURGE_Purge__AnchorMode=FiscalYearEnd",
  "PURGE_Purge__WindowStart=01:00",
  "PURGE_Purge__WindowEnd=05:00"
]));
add(rich(["La modalità non è configurabile. ", { b: 1 }],
  "Non esistono variabili PURGE_DRY_RUN o PURGE_DELETE: la scelta fra simulazione e cancellazione è una proprietà del comando, non dell'ambiente in cui gira (§9.1, livello 2). Senza modalità il comando non esegue nulla ed esce con codice 2."));

add(H2("10.3 Le due modalità di esecuzione"));
add(spacer());
add(table(
  ["Modalità", "Come si avvia", "Comportamento"],
  [
    ["Esecuzione singola", "purge once …", "Esegue una volta e termina, restituendo un codice di uscita. È la modalità pensata per uno scheduler esterno (UC4, cron, Task Scheduler), che decide quando eseguire e raccoglie l'esito."],
    ["Servizio", "purge  (senza argomenti)", "Resta in esecuzione e si autopianifica secondo CronExpression. È dry-run per costruzione: non può cancellare. Chiedere --delete in questa modalità viene rifiutato, non degradato in silenzio a simulazione."]
  ],
  [2100, 2500, 4800]
));
add(note("La finestra oraria vale in entrambe le modalità, anche nell'esecuzione singola. Non è ridondante: lo scheduler decide quando partire, ma solo la finestra decide quando fermarsi. Un avvio fuori finestra esce con codice 4 senza fare nulla, e il log segnala che pianificazione e finestra configurata non concordano."));

add(H2("10.4 I comandi, uno per uno"));
add(spacer());
add(table(
  ["Comando", "Effetto"],
  [
    ["purge migrate", "Applica gli script di schema mancanti. Richiede permessi DDL."],
    ["purge migrate --status", "Elenca cosa manca senza applicare nulla. Esce con 0 se allineato, 5 altrimenti: utilizzabile come controllo automatico."],
    ["purge once --dry-run", "Simula tutte le strategie configurate. Nessuna cancellazione, produce il report."],
    ["purge once --dry-run Terminated", "Simula una sola strategia."],
    ["purge approve <run-id> --by <nome> [--note <rif>]", "Registra l'approvazione della policy sotto cui è girato quel dry-run."],
    ["purge once --delete", "Esecuzione reale, tutte le strategie configurate."],
    ["purge once --delete OrphanHistory", "Esecuzione reale di una sola strategia."],
    ["purge once --delete --no-window", "Esecuzione reale senza il limite di fine finestra."],
    ["purge", "Servizio con pianificazione interna, in sola simulazione."]
  ],
  [4200, 5200], [0]
));
add(rich("Il nome della strategia, quando presente, va subito dopo la modalità e non distingue maiuscole e minuscole. I valori ammessi sono ",
  ["Terminated", { mono: 1 }], ", ", ["StandingOrders", { mono: 1 }], ", ", ["Collective", { mono: 1 }], ", ",
  ["OrphanHistory", { mono: 1 }], ", ", ["Abandoned", { mono: 1 }],
  ". Omettendolo si eseguono tutte le strategie configurate, in sequenza, ciascuna come run separato con un proprio identificatore."));
add(rich(["--no-window", { mono: 1 }], " rinuncia al limite di fine finestra. Esiste per i recuperi e i collaudi, viene registrato nel log come richiesta esplicita, e con ",
  ["--delete", { mono: 1 }], " in produzione non andrebbe usato senza una ragione scritta: la finestra è ciò che tiene il purge lontano dall'operatività."));
add(note("Un'istanza alla volta: all'avvio il motore prende un lock applicativo sul database. Se un'altra istanza sta già girando, il processo esce subito con codice 0 e un avviso nel log — non fallisce, perché una sovrapposizione dello scheduler non è un errore da segnalare come tale."));

add(H2("10.5 Sessioni tipo"));
add(H3("Prima installazione su un database nuovo"));
add(code([
  "purge migrate --status                            # cosa manca",
  "purge migrate                                     # applica",
  "purge once --dry-run OrphanHistory --no-window    # prova di fumo, di giorno",
  "purge once --dry-run                              # simulazione completa, di notte"
]));
add(H3("Dalla simulazione alla prima cancellazione"));
add(code([
  "purge once --dry-run                              # produce il report",
  "#    [ esame del report con Compliance ]",
  "purge approve 3f2a... --by \"M. Rossi\" --note \"CR-1487\"",
  "#    [ il DBA concede DELETE all'utenza del purge ]",
  "purge once --delete OrphanHistory                 # prima notte, una strategia",
  "purge once --delete                               # a regime, tutte"
]));
add(H3("Un run interrotto che riprende"));
add(P("Nessun comando speciale: si rilancia lo stesso comando. Il motore trova il run non concluso e riparte dal checkpoint, sullo stesso insieme di candidati congelato."));
add(code([
  "purge once --delete                               # esce con 5: finestra superata",
  "#    [ la notte successiva, stesso comando ]",
  "purge once --delete                               # riprende da dove si era fermato"
]));

add(H2("10.6 Codici di uscita"));
add(P("L'esecuzione singola comunica l'esito allo scheduler tramite il codice di uscita. Due di questi non sono guasti."));
add(spacer());
add(table(
  ["Codice", "Significato", "Azione"],
  [
    ["0", "Concluso", "Analisi del giorno dopo"],
    ["1", "Una strategia è fallita", "Esaminare l'errore: il run non riprende"],
    ["2", "Modalità assente, configurazione o schema non validi", "Nulla è stato cancellato"],
    ["4", "Avvio fuori dalla finestra oraria", "Nulla è stato cancellato"],
    ["5", "Finestra superata durante una fase lunga", "Non è un guasto: riprende la notte dopo"],
    ["130", "Interrotto (Ctrl-C o arresto del job)", "Non è un guasto: riprende dal checkpoint"]
  ],
  [1000, 4600, 3800]
));
add(note("Sui volumi di produzione le prime notti finiranno quasi sempre con 5. Chi sorveglia i job va avvisato in anticipo, altrimenti il primo 5 viene trattato come un incidente."));

add(H2("10.7 Osservabilità"));
add(P("Il motore emette eventi di log con nomi stabili, pensati per essere interrogati: avvio e conclusione del run, selezione completata, collettivo escluso, slice divisa, slice fallita, run interrotto. Le metriche esposte contano righe cancellate, slice completate, abbandonate e divise, e misurano la durata delle slice."));
add(rich("Due segnali meritano un allarme: una crescita di ", ["purge.slices_abandoned", { mono: 1 }],
  " indica dati che rifiutano la cancellazione; una crescita regolare di ", ["purge.slices_split", { mono: 1 }],
  " indica che i dati cambiano fra selezione ed esecuzione più spesso di quanto il disegno assuma — ed è quello il problema da guardare, non la bisezione."));

add(H2("10.8 Procedure operative"));
add(P("Due documenti separati coprono l'esecuzione, con il dettaglio passo per passo che qui sarebbe fuori luogo:"));
add(bullet("docs/runbook-primo-dry-run.md — la prima simulazione in produzione: prerequisiti, permessi, verifiche preliminari, lettura del report, approvazione."));
add(bullet("docs/runbook-prima-esecuzione-reale.md — la prima cancellazione: backup verificato, taratura, sorveglianza della prima notte, analisi del giorno dopo, messa a regime."));
add(P("Due script di analisi, in sola lettura, accompagnano i runbook: uno fotografa il database prima (volumi, pesi, anomalie, indici), l'altro legge i risultati di un run (report, validazioni, esclusioni, slice, staging)."));
add(pageBreak());

// ================================================================ 11
add(H1("11. Decisioni architetturali"));
add(P("Ogni riga di questa tabella corrisponde a una scelta in cui la soluzione ovvia era sbagliata. La colonna che conta è la terza: senza di essa, la decisione sembra arbitraria e prima o poi qualcuno la annulla in buona fede."));
add(spacer());
add(table(
  ["ID", "Decisione", "Alternativa scartata, e perché"],
  [
    ["D-1", "Paginazione per chiave (keyset)", "OFFSET/FETCH: alla millesima pagina il server scorre e scarta le precedenti."],
    ["D-2", "Audit nella stessa transazione delle DELETE", "Audit scritto dopo il commit: una caduta fra i due lascerebbe cancellazioni non registrate."],
    ["D-3", "Il collettivo è atomico", "Cancellare i componenti eleggibili: lascerebbe una testata contabile che punta al nulla."],
    ["D-4", "Dry-run privo di percorsi distruttivi", "Un flag che salta le DELETE: un errore di configurazione basterebbe a cancellare."],
    ["D-5", "La fase è il checkpoint", "Una tabella di avanzamento separata: potrebbe divergere dallo stato reale."],
    ["D-6", "Coordinatore separato dalla fase", "Retry e finestra dentro ExecutingPhase: politica e workflow mescolati, nessuno dei due testabile."],
    ["D-7", "Classificazione degli errori in quattro categorie", "Un catch generico: un deadlock e un difetto riceverebbero la stessa risposta."],
    ["D-8", "Pianificazione in due passate", "Un CASE nella query: renderebbe il predicato non sargable e inutilizzabili gli indici."],
    ["D-9", "Finestra a due livelli con tolleranza", "Solo il controllo fra slice: una fase lunga sforerebbe senza accorgersene."],
    ["D-10", "Collettivi anomali esclusi in selezione", "Degradare la validazione: una rete che scarta in silenzio non è più una rete."],
    ["D-11", "Bisezione della slice", "Abbandono in blocco (perde fino a 500 aggregati) o ripianificazione in singoli (N transazioni invece di 2·log₂N)."],
    ["D-12", "Il cambio di stato in corsa non si riprova", "Retry: il conteggio riflette uno stato già committato, riprovare conferma lo stesso esito."],
    ["D-13", "Migrazioni tracciate con script SQL", "Migrazioni EF Core: viste, indici filtrati e ALTER diventerebbero SQL dentro C#, non più rivedibile dal DBA."],
    ["D-14", "Mappatura per nome di colonna", "Lettura per posizione: una colonna aggiunta in mezzo è un errore silenzioso."],
    ["B-1", "Packer separato e privo di I/O", "Packing dentro il planner: non verificabile senza database."],
    ["B-2", "Reader chiuso prima della bulk copy", "Reader aperto: conflitto sulla stessa sessione SQL."],
    ["HK-1", "Housekeeping distinto dal purge di dominio", "Un'unica procedura: audit e staging hanno esigenze di conservazione opposte."]
  ],
  [700, 3100, 5600]
));
add(note("Il registro completo, con il ragionamento esteso di ciascuna decisione, è in docs/decisioni.md. Questa tabella ne è la sintesi."));
add(pageBreak());

// ================================================================ 12
add(H1("12. Punti aperti"));
add(P("Ciò che segue non è incompleto per dimenticanza: sono decisioni che spettano a interlocutori esterni allo sviluppo, e che il sistema oggi gestisce nel modo più prudente possibile in attesa di una risposta."));
add(spacer());
add(table(
  ["ID", "Questione", "Interlocutore", "Comportamento attuale"],
  [
    ["PA-3", "I cinque anni decorrono dalla data dell'operazione o dalla chiusura d'esercizio?", "Compliance / Legal", "Ancoraggio all'esercizio: l'ipotesi più conservativa"],
    ["PA-4", "Cinque anni sono sufficienti per tutte le categorie di ordine?", "Compliance / Legal", "Soglia unica per tutte le categorie"],
    ["PA-5", "Serve un meccanismo di blocco per contenzioso (legal hold)?", "Legal", "Non implementato: nessun ordine è esentabile"],
    ["PA-7", "Collettivi privi di data di esecuzione", "Business", "Esclusi e censiti: mai cancellati"],
    ["PA-21", "Da quanto tempo un ordine mai completato può essere rimosso?", "Business / Compliance", "Strategia disattivata"],
    ["—", "Un piano ricorrente può essere componente di un collettivo?", "Business", "Non escluso esplicitamente: da chiarire prima dei volumi grandi"],
    ["—", "Retention per l'audit trail", "Compliance", "Nessuna: audit e tracce conservati senza limite"]
  ],
  [700, 3700, 1900, 3100]
));
add(pageBreak());

// ================================================================ 13
add(H1("13. Checklist di messa in produzione"));
add(P("Da compilare una volta, prima della prima esecuzione reale. Ogni voce ha un responsabile e una prova documentale."));
add(spacer());
add(check("Retention e modo di ancoraggio confermati da Compliance (PA-3, PA-4)."));
add(check("Verifica delle foreign key eseguita: la topologia reale coincide con quella attesa."));
add(check("Controlli preliminari sui dati eseguiti e anomalie esaminate."));
add(check("Indici di supporto creati sulle tabelle applicative."));
add(check("Schema Purge allineato: purge migrate --status non riporta script mancanti."));
add(check("Dry-run concluso per ogni strategia attiva; report esaminato e accettato."));
add(check("Collettivi esclusi esaminati con il referente applicativo."));
add(check("Policy approvata e registrata (purge approve)."));
add(check("Backup completo verificato con ripristino di prova."));
add(check("Spazio del log transazionale verificato per il volume previsto."));
add(check("Permesso di cancellazione concesso alla sola utenza del purge, dopo l'approvazione."));
add(check("Dimensione delle slice e finestra oraria tarate sui numeri reali."));
add(check("Ripresa dopo interruzione verificata su ambiente di test."));
add(check("Allarmi configurati su slice abbandonate, slice divise e durata."));
add(check("Retention dello staging tarata."));
add(check("Prima notte pianificata su una sola strategia, con DBA reperibile."));
add(pageBreak());

// ================================================================ appendici
add(H1("Appendice A — Mappa del repository"));
add(spacer());
add(table(
  ["Percorso", "Contenuto"],
  [
    ["src/OSM.PaymentOrder.Purge", "Il motore: strategie, fasi, planning, esecuzione, SQL"],
    ["src/OSM.PaymentOrder.Purge.Host", "Host: riga di comando, pianificazione, migrazioni"],
    ["tests/OSM.PaymentOrder.Purge.Tests", "Test unitari e di integrazione su SQL Server reale"],
    ["db/000_install_purge.sql", "Installazione autonoma dello schema Purge"],
    ["db/001 … 012", "Migrazioni incrementali, applicate da purge migrate"],
    ["db/002_indexes.sql", "Indici sulle tabelle applicative (applicazione manuale)"],
    ["db/003_preflight.sql, 004_verify_fk.sql", "Verifiche preliminari sui dati e sulla topologia"],
    ["db/007, 009", "Verifiche di atomicità collettiva e di coerenza dell'audit"],
    ["db/020_analisi_pre_dry_run.sql", "Fotografia del database prima della simulazione"],
    ["db/021_analisi_post_dry_run.sql", "Lettura dei risultati di un run"],
    ["docs/decisioni.md", "Registro esteso delle decisioni architetturali"],
    ["docs/runbook-primo-dry-run.md", "Procedura della prima simulazione"],
    ["docs/runbook-prima-esecuzione-reale.md", "Procedura della prima cancellazione"],
    ["docs/changelog/", "Storia delle versioni"]
  ],
  [3600, 5800], [0]
));

add(H1("Appendice B — Parametri di configurazione"));
add(P("I valori indicati sono i default. Quelli marcati vanno tarati sui numeri reali prima della prima esecuzione."));
add(spacer());
add(table(
  ["Parametro", "Default", "Effetto"],
  [
    ["RetentionYears", "5", "Anni di conservazione. Fa parte della policy approvata."],
    ["AnchorMode", "FiscalYearEnd", "Modo di ancoraggio della soglia. Fa parte della policy approvata."],
    ["Strategies", "tutte tranne Abandoned", "Strategie attive. Fa parte della policy approvata."],
    ["MaxRowsPerBatch  ▲", "3000", "Tetto di righe per slice: tiene la transazione sotto la soglia di lock escalation."],
    ["MaxOrdersPerBatch  ▲", "500", "Tetto di aggregati per slice."],
    ["SelectionBatchSize", "4000", "Righe lette per pagina in selezione ed espansione."],
    ["MaxSliceAttempts", "3", "Tentativi su una slice in caso di contesa."],
    ["MaxSplitDepth", "10", "Bisezioni massime prima di abbandonare. Zero disattiva la bisezione."],
    ["MaxRunInterruptions", "5", "Interruzioni tollerate prima di dichiarare il run fallito."],
    ["InterSliceDelay  ▲", "100 ms", "Pausa fra una slice e l'altra: è la leva principale sull'impatto all'operatività."],
    ["WindowStart / WindowEnd  ▲", "01:00 / 05:00", "Finestra oraria, nell'ora locale dell'host."],
    ["StagingRetentionDays", "7", "Conservazione dello staging dei run conclusi senza incidenti."],
    ["FailedStagingRetentionDays", "90", "Conservazione dello staging dei run falliti o con abbandoni."],
    ["AbandonedEnabled", "false", "Strategia degli ordini mai completati (PA-21)."],
    ["CommandTimeoutSeconds", "300", "Timeout dei comandi SQL."]
  ],
  [3000, 1500, 4900], [0]
));
add(note("▲ = da tarare sui numeri reali prima della prima esecuzione."));

// ---------------------------------------------------------------- documento
const doc = new Document({
  creator: "OSM.PaymentOrder Purge",
  title: "Architecture & Functional Design — Sfoltimento dati OSM.PaymentOrder",
  description: "Disegno funzionale e tecnico del motore di retention",
  styles: {
    default: {
      document: { run: { font: "Calibri", size: 21, color: "1A1A1A" } }
    },
    paragraphStyles: [
      { id: "Heading1", name: "Heading 1", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { font: "Calibri", size: 30, bold: true, color: ACCENT } },
      { id: "Heading2", name: "Heading 2", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { font: "Calibri", size: 24, bold: true, color: ACCENT } },
      { id: "Heading3", name: "Heading 3", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { font: "Calibri", size: 22, bold: true, color: "2E4570" } }
    ]
  },
  numbering: {
    config: [{
      reference: "punti",
      levels: [
        { level: 0, format: LevelFormat.BULLET, text: "\u2022", alignment: AlignmentType.LEFT,
          style: { paragraph: { indent: { left: 420, hanging: 220 } } } },
        { level: 1, format: LevelFormat.BULLET, text: "\u2013", alignment: AlignmentType.LEFT,
          style: { paragraph: { indent: { left: 780, hanging: 220 } } } }
      ]
    }]
  },
  features: { updateFields: true },
  sections: [{
    properties: {
      page: {
        size: { width: 11906, height: 16838 },
        margin: { top: 1200, bottom: 1100, left: 1200, right: 1200 }
      }
    },
    headers: {
      default: new Header({ children: [new Paragraph({
        alignment: AlignmentType.RIGHT, spacing: { after: 120 },
        border: { bottom: { style: BorderStyle.SINGLE, size: 4, color: "C9D3E0", space: 6 } },
        children: [new TextRun({ text: "Sfoltimento dati OSM.PaymentOrder — Architecture & Functional Design",
                                 size: 16, color: "7A8699" })] })] })
    },
    footers: {
      default: new Footer({ children: [new Paragraph({
        alignment: AlignmentType.CENTER,
        children: [new TextRun({ children: ["", PageNumber.CURRENT], size: 16, color: "7A8699" })] })] })
    },
    children
  }]
});

Packer.toBuffer(doc).then(b => {
  fs.writeFileSync("Architecture_Functional_Design_Sfoltimento.docx", b);
  console.log("scritto", b.length, "byte");
});
