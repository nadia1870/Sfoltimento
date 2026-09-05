<#
    Aggiunge un'asserzione diagnostica al test sui piani ricorrenti.

    Serve a distinguere due cause possibili del fallimento:
      - il seed non valorizza LastExecutionDate  -> difetto nel test
      - il motore non seleziona il piano terminato -> difetto nel motore

    L'asserzione va PRIMA dell'esecuzione del run, cosi' fallisce sul seed
    quando e' il seed a essere sbagliato.

    Da eseguire dalla radice del repository:
        powershell -ExecutionPolicy Bypass -File .\add-standingorder-assert.ps1
#>

$ErrorActionPreference = 'Stop'

$tests = 'tests\OSM.PaymentOrder.Purge.Tests\RetentionInvariantsTests.cs'
if (-not (Test-Path $tests)) {
    throw "File non trovato: $tests. Eseguire lo script dalla radice del repository."
}

Copy-Item $tests "$tests.diag.bak" -Force
Write-Host "Backup: $tests.diag.bak" -ForegroundColor DarkGray

$text = Get-Content $tests -Raw

if ($text -match 'DIAGNOSTICA piani ricorrenti') {
    Write-Host "Asserzione gia' presente, salto." -ForegroundColor Yellow
}
else {
    # ancora: la riga che esegue il run nel test dei piani ricorrenti
    $anchor = '        await RunAsync(RetentionStrategy.StandingOrders);'

    if ($text -notmatch [regex]::Escape($anchor)) {
        throw "Punto di innesto non trovato. Aggiungere l'asserzione a mano prima di RunAsync(RetentionStrategy.StandingOrders)."
    }

    $diag = @'
        // --- DIAGNOSTICA piani ricorrenti -------------------------------
        // Se questa asserzione fallisce il difetto e' nel seed: AddOrderAsync
        // inserisce la riga in StandingOrder e la aggiorna solo dopo, quindi
        // un UPDATE senza effetto lascerebbe entrambi i piani "attivi" e
        // nessuno dei due verrebbe selezionato.
        var conLastExec = await db.Sql.ScalarAsync<long>(
            "SELECT COUNT_BIG(*) FROM PaymentOrder.StandingOrder WHERE LastExecutionDate IS NOT NULL;",
            default);
        Assert.Equal(1, conLastExec);

        // Stato completo, utile in caso di fallimento successivo.
        var righe = await db.Sql.QueryAsync("""
            SELECT o.Code, o.StatusCode, o.StandingOrder, o.ExecutionDate, so.LastExecutionDate
            FROM PaymentOrder.[Order] o
            LEFT JOIN PaymentOrder.StandingOrder so ON so.OrderId = o.Id;
            """,
            r => $"{r.GetString(0)} stato={r.GetString(1)} SO={r.GetBoolean(2)} " +
                 $"exec={r.GetDateTime(3):yyyy-MM-dd} " +
                 $"last={(r.IsDBNull(4) ? "NULL" : r.GetDateTime(4).ToString("yyyy-MM-dd"))}",
            default);

        var cutoff = db.Options.ComputeRetentionCutoff(DateTimeOffset.Now);
        var dettaglio = $"cutoff={cutoff:yyyy-MM-dd} mode={db.Options.AnchorMode}\n" +
                        string.Join("\n", righe);
        // ----------------------------------------------------------------

'@

    $replacement = $diag + $anchor
    $text = $text -replace [regex]::Escape($anchor), $replacement

    # l'asserzione finale riporta il dettaglio in caso di fallimento
    $oldAssert = @'
        Assert.Equal(1, await Seed.CountAsync("[Order]"));
        Assert.Equal(1, await Seed.CountAsync("StandingOrder"));
'@
    $newAssert = @'
        Assert.True(1 == await Seed.CountAsync("[Order]"),
            $"Ordini attesi 1, trovati {await Seed.CountAsync("[Order]")}.\n{dettaglio}");
        Assert.Equal(1, await Seed.CountAsync("StandingOrder"));
'@

    if ($text -match [regex]::Escape($oldAssert)) {
        $text = $text -replace [regex]::Escape($oldAssert), $newAssert
        Write-Host "Asserzione finale arricchita con il dettaglio" -ForegroundColor Green
    }
    else {
        Write-Host "Asserzione finale non riconosciuta: lasciata invariata." -ForegroundColor Yellow
    }

    Set-Content $tests $text -NoNewline -Encoding UTF8
    Write-Host "Asserzione diagnostica aggiunta" -ForegroundColor Green
}

Write-Host ""
Write-Host "Eseguire ora:" -ForegroundColor Cyan
Write-Host '  dotnet test --filter "FullyQualifiedName~Piano_ricorrente"' -ForegroundColor Cyan
Write-Host ""
Write-Host "Lettura del risultato:" -ForegroundColor Cyan
Write-Host "  fallisce su 'conLastExec'  -> difetto nel seed del test" -ForegroundColor DarkGray
Write-Host "  fallisce dopo, con dettaglio -> difetto nella selezione del motore" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Ripristino: Copy-Item `"$tests.diag.bak`" `"$tests`" -Force" -ForegroundColor DarkGray
