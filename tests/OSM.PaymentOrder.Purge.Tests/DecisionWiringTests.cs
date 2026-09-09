using System.Reflection;
using OSM.PaymentOrder.Purge.Engine;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// Verifica che alcune decisioni siano davvero collegate al percorso di
/// esecuzione, non solo implementate.
///
/// Nasce da una revisione esterna che sosteneva — sulla base di una copia
/// vecchia del repository — che il gate di approvazione e la modalità da riga
/// di comando esistessero ma non fossero invocati. L'affermazione era falsa,
/// ma non era falsificabile: nessun test diceva che quel collegamento
/// esistesse, e nessuno si sarebbe accorto se qualcuno lo avesse tolto.
///
/// Un componente di sicurezza scollegato è indistinguibile da uno assente, e
/// scollegarlo è un'operazione di una riga. Questi test rendono la riga
/// rumorosa.
///
/// Sono controlli sul testo del sorgente: grezzi, ma sufficienti a rilevare la
/// rimozione di un collegamento, e senza il costo di far girare l'host.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DecisionWiringTests
{
    private static string Sorgente(string nomeFile)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OSM.PaymentOrder.Purge.sln")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException("Radice del repository non trovata da " + AppContext.BaseDirectory);

        var file = dir.GetFiles(nomeFile, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException($"{nomeFile} non trovato nel repository.");

        return File.ReadAllText(file.FullName);
    }

    /// <summary>
    /// D-4, livello 4: il gate viene interrogato prima di eseguire, e un
    /// rifiuto termina con un codice diverso da zero. Uno skip silenzioso
    /// sembrerebbe un successo allo scheduler.
    /// </summary>
    [Fact]
    public void Il_gate_di_approvazione_e_invocato_prima_dell_esecuzione()
    {
        var program = Sorgente("Program.cs");

        Assert.Contains("GetRequiredService<PurgeApprovalGate>()", program);
        Assert.Contains("gate.IsAllowedAsync(", program);
        Assert.Contains("AddSingleton<PurgeApprovalGate>()", program);

        // L'invocazione precede l'avvio dell'host e l'esecuzione singola.
        var posizioneGate = program.IndexOf("gate.IsAllowedAsync(", StringComparison.Ordinal);
        var posizioneEsecuzione = program.IndexOf("host.RunAsync()", StringComparison.Ordinal);
        Assert.True(posizioneGate < posizioneEsecuzione,
            "Il gate deve essere interrogato prima di avviare l'esecuzione.");
    }

    /// <summary>
    /// D-4, livello 2: la modalità arriva dalla riga di comando e sovrascrive
    /// la configurazione. Un file modificato per errore non deve poter
    /// trasformare una simulazione in una cancellazione.
    /// </summary>
    [Fact]
    public void La_modalita_arriva_dalla_riga_di_comando()
    {
        var program = Sorgente("Program.cs");

        Assert.Contains("PurgeExecutionModeParser.Parse(args)", program);
        Assert.Contains("opzioni.DryRun = mode == PurgeExecutionMode.DryRun", program);
    }

    /// <summary>
    /// D-17: il tetto per aggregato deve arrivare fino al packer. Fra
    /// l'opzione e la decisione ci sono tre passaggi, e il valore di default
    /// del parametro — zero, cioè disattivato — rende silenzioso un
    /// collegamento mancante.
    /// </summary>
    [Fact]
    public void Il_tetto_per_aggregato_arriva_dalla_configurazione_al_packer()
    {
        Assert.Contains("maxAggregateWeight:", Sorgente("Program.cs"));
        Assert.Contains("_maxAggregateWeight", Sorgente("BatchPlanner.cs"));

        // E l'opzione esiste con un default che non lo disattiva.
        Assert.True(new PurgeOptions().MaxAggregateWeight > 0);
    }

    /// <summary>
    /// D-16: la pianificazione del servizio usa il fuso dichiarato, non quello
    /// dell'host. Erano rimasti disallineati — finestra sì, cron no — e su un
    /// host in UTC il servizio si sarebbe svegliato due ore dopo l'apertura
    /// della finestra.
    /// </summary>
    [Fact]
    public void Il_cron_usa_il_fuso_dichiarato()
    {
        var cron = Sorgente("RetentionCronService.cs");

        Assert.Contains("_options.TimeZone", cron);
        Assert.DoesNotContain("TimeZoneInfo.Local", cron);
    }

    /// <summary>
    /// D-16: nessun punto legge più l'orologio locale dell'host direttamente.
    /// PurgeOptions è l'unico posto in cui TimeZoneInfo.Local può comparire,
    /// come ripiego dichiarato.
    /// </summary>
    [Fact]
    public void Nessuno_legge_l_orologio_locale_dell_host()
    {
        foreach (var file in new[]
                 { "Program.cs", "RetentionCronService.cs", "PurgeWindowGuard.cs",
                   "PurgeHousekeeping.cs", "BatchExecutionCoordinator.cs" })
        {
            Assert.DoesNotContain("GetLocalNow()", Sorgente(file));
        }
    }

    /// <summary>
    /// La finestra vale anche nell'esecuzione singola: lo scheduler decide
    /// quando partire, la finestra decide quando fermarsi. L'unico modo di
    /// rinunciarvi è chiederlo esplicitamente.
    /// </summary>
    [Fact]
    public void La_finestra_si_disattiva_solo_su_richiesta_esplicita()
    {
        var program = Sorgente("Program.cs");

        var disattivazioni = program.Split("options.WindowEnabled = false").Length - 1;
        Assert.Equal(1, disattivazioni);

        // L'unica disattivazione sta nel ramo del flag, ed e' registrata nel log.
        var posizioneFlag = program.IndexOf("var senzaFinestra", StringComparison.Ordinal);
        var posizioneDisattivazione = program.IndexOf("options.WindowEnabled = false", StringComparison.Ordinal);
        Assert.True(posizioneFlag >= 0 && posizioneFlag < posizioneDisattivazione);
        Assert.Contains("PurgeWindowDisabled", program);
    }

    /// <summary>
    /// D-20: un'esecuzione reale con la finestra attiva pretende un fuso
    /// dichiarato. Senza, la finestra sarebbe definita dal fuso della
    /// macchina, che non e' una decisione di nessuno.
    ///
    /// Il controllo sta prima del gate: rifiutare per configurazione mancante
    /// e' piu' preciso che rifiutare per policy non approvata, e i due motivi
    /// non vanno confusi nel messaggio.
    ///
    /// Questo test verifica solo che la guardia sia invocata e dove. Che dica
    /// la cosa giusta lo verifica StartupGuardTests, per comportamento: la
    /// distinzione conta, perche' la prima versione della guardia era
    /// invocata nel posto giusto e sbagliava la condizione.
    /// </summary>
    [Fact]
    public void L_esecuzione_reale_pretende_un_fuso_dichiarato()
    {
        var program = Sorgente("Program.cs");

        Assert.Contains("PurgeStartupGuard.RejectionReason(mode, opzioni, args)", program);

        var posizioneControllo = program.IndexOf("PurgeStartupGuard.RejectionReason",
            StringComparison.Ordinal);
        var posizioneGate = program.IndexOf("gate.IsAllowedAsync(", StringComparison.Ordinal);
        Assert.True(posizioneControllo < posizioneGate,
            "Il controllo sul fuso deve precedere il gate: sono due rifiuti diversi.");
    }

    /// <summary>
    /// D-14: le interfacce del livello dati non devono esporre Dapper. È ciò
    /// che rende sostituibile il livello dati senza toccare il motore.
    /// </summary>
    [Fact]
    public void Il_motore_non_conosce_Dapper()
    {
        var motore = typeof(RetentionOrchestrator).Assembly;

        var tipiEsposti = motore.GetTypes()
            .Where(t => t.IsPublic && t.Namespace?.EndsWith(".Engine", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))
            .Select(t => t.Namespace ?? "")
            .Where(n => n.StartsWith("Dapper", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(tipiEsposti);
    }
}
