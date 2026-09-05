<#
    Correzione del seed dei piani ricorrenti.

    AddOrderAsync inseriva il dettaglio nella tabella indicata da detailTable,
    il cui default e' "BankTransfer". Con standingOrder: true la riga in
    PaymentOrder.StandingOrder non veniva quindi creata, e l'UPDATE successivo
    su LastExecutionDate non trovava nulla da aggiornare: zero righe modificate,
    nessun errore, e il test falliva senza indicare la vera causa.

    Da eseguire dalla radice del repository:
        powershell -ExecutionPolicy Bypass -File .\fix-seed-standingorder.ps1
#>

$ErrorActionPreference = 'Stop'

$seed = 'tests\OSM.PaymentOrder.Purge.Tests\SeedBuilder.cs'
if (-not (Test-Path $seed)) {
    throw "File non trovato: $seed. Eseguire lo script dalla radice del repository."
}

Copy-Item $seed "$seed.bak" -Force
Write-Host "Backup: $seed.bak" -ForegroundColor DarkGray

$text = Get-Content $seed -Raw

if ($text -match 'effectiveDetail') {
    Write-Host "Correzione gia' applicata, salto." -ForegroundColor Yellow
}
else {
    # ------------------------------------------------ 1. tabella di dettaglio
    $old = @'
        var detailId = Guid.NewGuid();
        await sql.ExecuteAsync(
            $"INSERT INTO PaymentOrder.{detailTable} (Id, OrderId) VALUES (@Id, @OrderId);",
            default, SqlParam.Of("@Id", detailId), SqlParam.Of("@OrderId", id));
'@

    $new = @'
        // Un ordine ricorrente ha il proprio dettaglio in StandingOrder: senza
        // questo, con standingOrder: true la riga non veniva creata e l'UPDATE
        // successivo su LastExecutionDate non trovava nulla da aggiornare.
        var effectiveDetail = standingOrder ? "StandingOrder" : detailTable;

        var detailId = Guid.NewGuid();
        await sql.ExecuteAsync(
            $"INSERT INTO PaymentOrder.{effectiveDetail} (Id, OrderId) VALUES (@Id, @OrderId);",
            default, SqlParam.Of("@Id", detailId), SqlParam.Of("@OrderId", id));
'@

    if ($text -notmatch [regex]::Escape($old)) {
        throw "Blocco di inserimento del dettaglio non trovato: correggere a mano."
    }
    $text = $text -replace [regex]::Escape($old), $new
    Write-Host "1. Tabella di dettaglio corretta per i piani ricorrenti" -ForegroundColor Green

    # -------------------------------- 2. verifica che l'UPDATE abbia effetto
    $oldUpdate = @'
            await sql.ExecuteAsync("""
                UPDATE PaymentOrder.StandingOrder
                   SET FirstExecutionDate = @First, LastExecutionDate = @Last
                 WHERE OrderId = @OrderId;
                """, default,
                SqlParam.Of("@First", exec), SqlParam.Of("@Last", (object?)lastExecutionDate),
                SqlParam.Of("@OrderId", id));
'@

    $newUpdate = @'
            var updated = await sql.ExecuteAsync("""
                UPDATE PaymentOrder.StandingOrder
                   SET FirstExecutionDate = @First, LastExecutionDate = @Last
                 WHERE OrderId = @OrderId;
                """, default,
                SqlParam.Of("@First", exec), SqlParam.Of("@Last", (object?)lastExecutionDate),
                SqlParam.Of("@OrderId", id));

            // Un UPDATE che non trova la riga non produce errori: e' il motivo
            // per cui il difetto precedente si manifestava solo come conteggio
            // sbagliato, molto piu' a valle.
            if (updated != 1)
            {
                throw new InvalidOperationException(
                    $"Seed incoerente: attesa 1 riga in StandingOrder per l'ordine {id}, " +
                    $"aggiornate {updated}.");
            }
'@

    if ($text -match [regex]::Escape($oldUpdate)) {
        $text = $text -replace [regex]::Escape($oldUpdate), $newUpdate
        Write-Host "2. Verifica sull'esito dell'UPDATE aggiunta" -ForegroundColor Green
    }
    else {
        Write-Host "2. Blocco UPDATE non riconosciuto: lasciato invariato." -ForegroundColor Yellow
    }

    # --------------------------- 3. gli storici vanno nella stessa tabella
    $oldRev = 'await AddRevisionAsync(id, code, statusCode, detailTable, detailId);'
    $newRev = 'await AddRevisionAsync(id, code, statusCode, effectiveDetail, detailId);'

    if ($text -match [regex]::Escape($oldRev)) {
        $text = $text -replace [regex]::Escape($oldRev), $newRev
        Write-Host "3. Storici di dettaglio allineati alla tabella corretta" -ForegroundColor Green
    }
    else {
        Write-Host "3. Chiamata ad AddRevisionAsync non riconosciuta: verificare a mano." -ForegroundColor Yellow
    }

    Set-Content $seed $text -NoNewline -Encoding UTF8
}

Write-Host ""
Write-Host "Verifica:" -ForegroundColor Cyan
Select-String -Path $seed -Pattern 'effectiveDetail|updated != 1' |
    ForEach-Object { "  riga $($_.LineNumber): $($_.Line.Trim())" }

Write-Host ""
Write-Host "Eseguire ora:" -ForegroundColor Cyan
Write-Host '  dotnet test --filter "FullyQualifiedName~Piano_ricorrente"' -ForegroundColor Cyan
Write-Host "  poi: dotnet test" -ForegroundColor Cyan
Write-Host ""
Write-Host "Ripristino: Copy-Item `"$seed.bak`" `"$seed`" -Force" -ForegroundColor DarkGray
