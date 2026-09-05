<#
    Correzione dell'isolamento fra test.

    Il database e' condiviso dalla collezione xUnit: senza una pulizia prima di
    ogni test i conteggi dipendono dall'ordine di esecuzione. Tre test
    fallivano per questo, non per un difetto del motore.

    Da eseguire dalla radice del repository:
        .\fix-test-isolation.ps1
#>

$ErrorActionPreference = 'Stop'

$testDir  = 'tests\OSM.PaymentOrder.Purge.Tests'
$fixture  = Join-Path $testDir 'PurgeDatabaseFixture.cs'
$tests    = Join-Path $testDir 'RetentionInvariantsTests.cs'

foreach ($f in @($fixture, $tests)) {
    if (-not (Test-Path $f)) {
        throw "File non trovato: $f. Eseguire lo script dalla radice del repository."
    }
}

# ---------------------------------------------------------------- backup
Copy-Item $fixture "$fixture.bak" -Force
Copy-Item $tests   "$tests.bak"   -Force
Write-Host "Backup creati (.bak)" -ForegroundColor DarkGray

# ---------------------------------------------------- 1. ResetAsync nella fixture
$fixtureText = Get-Content $fixture -Raw

if ($fixtureText -match 'public async Task ResetAsync') {
    Write-Host "ResetAsync gia' presente, salto." -ForegroundColor Yellow
}
else {
    $reset = @'

    /// <summary>
    /// Il database e' condiviso dalla collezione: senza questa pulizia i test
    /// si contaminano a vicenda e le asserzioni sui conteggi diventano
    /// dipendenti dall'ordine di esecuzione.
    ///
    /// L'ordine delle DELETE e' quello topologico: le foglie per prime, come
    /// nel motore. Invertirlo produrrebbe violazioni di foreign key.
    /// </summary>
    public async Task ResetAsync()
    {
        string[] paymentOrderTables =
        [
            // gruppo 1 — storici di dettaglio, foglie del grafo
            "AccountTransferHistory", "BankTransferHistory",
            "BankTransferToCornerCardHistory", "ForeignBankTransferHistory",
            "InpaymentSlipHistory", "IpBankTransferHistory", "IpQRBillHistory",
            "QRBillHistory", "RealTimeCardReloadHistory", "StandingOrderHistory",
            "CollectiveOrderGroupOrderHistory",
            // gruppo 2
            "OrderHistory",
            // aggregato collettivo
            "CollectiveOrderGroupHistory", "CollectiveOrderGroupOrder",
            "CollectiveOrderGroup", "CollectiveOrderContent",
            "CollectiveOrderHistory", "CollectiveOrder",
            // gruppo 3 — dettagli correnti
            "AccountTransfer", "BankTransfer", "BankTransferToCornerCard",
            "ForeignBankTransfer", "InpaymentSlip", "IpBankTransfer", "IpQRBill",
            "QRBill", "RealTimeCardReload", "StandingOrder",
            "Model", "Category",
            // gruppo 4
            "[Order]"
        ];

        foreach (var table in paymentOrderTables)
            await Sql.ExecuteAsync($"DELETE FROM PaymentOrder.{table};", default);

        string[] purgeTables =
        [
            "RunCandidateOrderHistory", "RunCandidateOrder", "RunCandidateCollective",
            "RunBatchProgress", "ValidationFinding", "DryRunReport", "PurgeAudit",
            "PurgeRun"
        ];

        foreach (var table in purgeTables)
            await Sql.ExecuteAsync($"DELETE FROM Purge.{table};", default);
    }
'@

    # inserimento prima della chiusura della classe PurgeDatabaseFixture,
    # individuata dall'ultimo metodo privato statico
    $anchor = '    private static async Task ExecuteOnMasterAsync'
    if ($fixtureText -notmatch [regex]::Escape($anchor)) {
        throw "Punto di innesto non trovato in PurgeDatabaseFixture.cs: correggere a mano."
    }

    $fixtureText = $fixtureText -replace [regex]::Escape($anchor), ($reset + "`r`n" + $anchor)
    Set-Content $fixture $fixtureText -NoNewline -Encoding UTF8
    Write-Host "ResetAsync aggiunto alla fixture" -ForegroundColor Green
}

# --------------------------------------- 2. IAsyncLifetime sulla classe di test
$testsText = Get-Content $tests -Raw

if ($testsText -match 'IAsyncLifetime') {
    Write-Host "IAsyncLifetime gia' presente, salto." -ForegroundColor Yellow
}
else {
    $old = 'public sealed class RetentionInvariantsTests(PurgeDatabaseFixture db)'
    $new = @'
public sealed class RetentionInvariantsTests(PurgeDatabaseFixture db) : IAsyncLifetime
'@

    if ($testsText -notmatch [regex]::Escape($old)) {
        throw "Dichiarazione della classe non trovata: correggere a mano."
    }

    $testsText = $testsText -replace [regex]::Escape($old), $new.TrimEnd()

    # i due metodi del ciclo di vita, subito dopo l'apertura della classe
    $lifecycle = @'
{
    // xUnit invoca InitializeAsync prima di OGNI test della classe.
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;
'@

    # sostituisce la prima graffa di apertura della classe
    $idx = $testsText.IndexOf($new.TrimEnd())
    $brace = $testsText.IndexOf('{', $idx)
    if ($brace -lt 0) { throw "Graffa di apertura non trovata: correggere a mano." }

    $testsText = $testsText.Substring(0, $brace) + $lifecycle + $testsText.Substring($brace + 1)

    Set-Content $tests $testsText -NoNewline -Encoding UTF8
    Write-Host "IAsyncLifetime aggiunto alla classe di test" -ForegroundColor Green
}

# ---------------------------------------------------------------- verifica
Write-Host ""
Write-Host "Verifica:" -ForegroundColor Cyan
Select-String -Path $fixture -Pattern 'public async Task ResetAsync' |
    ForEach-Object { "  fixture riga $($_.LineNumber): $($_.Line.Trim())" }
Select-String -Path $tests -Pattern 'IAsyncLifetime|InitializeAsync' |
    ForEach-Object { "  test    riga $($_.LineNumber): $($_.Line.Trim())" }

Write-Host ""
Write-Host "Eseguire ora: dotnet test" -ForegroundColor Cyan
Write-Host "In caso di problemi, ripristinare da PurgeDatabaseFixture.cs.bak e RetentionInvariantsTests.cs.bak" -ForegroundColor DarkGray
