using System.Globalization;
using DbUp.Engine.Output;
using Microsoft.Extensions.Logging;

namespace OSM.PaymentOrder.Purge.Migrations;

/// <summary>
/// Porta il log di DbUp su ILogger, cosi' le migrazioni compaiono nello
/// stesso flusso del resto del motore.
///
/// IUpgradeLog e' cambiata fra le versioni di dbup-core: la 4 espone
/// WriteInformation/WriteWarning/WriteError, la 5 LogTrace/LogDebug/
/// LogInformation/LogWarning/LogError. Qui ci sono entrambe le famiglie: il
/// compilatore usa quelle che l'interfaccia della versione restaurata
/// richiede e le altre restano metodi pubblici innocui. E' l'unico modo di
/// non legare il file a un numero di versione preciso senza vederlo.
///
/// I messaggi di DbUp sono in formato string.Format ({0}), non template
/// strutturati: si compongono prima e si passano a ILogger come valore
/// unico, altrimenti una graffa nel nome di uno script verrebbe letta come
/// segnaposto.
/// </summary>
internal sealed class DbUpLogAdapter(ILogger log) : IUpgradeLog
{
    private static string Compose(string format, object[]? args) =>
        args is { Length: > 0 } ? string.Format(CultureInfo.InvariantCulture, format, args) : format;

    // ---- dbup-core 5.x
    public void LogTrace(string format, params object[] args) =>
        log.LogTrace("DbUp: {Message}", Compose(format, args));

    public void LogDebug(string format, params object[] args) =>
        log.LogDebug("DbUp: {Message}", Compose(format, args));

    public void LogInformation(string format, params object[] args) =>
        log.LogInformation("DbUp: {Message}", Compose(format, args));

    public void LogWarning(string format, params object[] args) =>
        log.LogWarning("DbUp: {Message}", Compose(format, args));

    public void LogError(string format, params object[] args) =>
        log.LogError("DbUp: {Message}", Compose(format, args));

    public void LogError(Exception ex, string format, params object[] args) =>
        log.LogError(ex, "DbUp: {Message}", Compose(format, args));

    // ---- dbup-core 4.x
    public void WriteInformation(string format, params object[] args) =>
        LogInformation(format, args);

    public void WriteWarning(string format, params object[] args) =>
        LogWarning(format, args);

    public void WriteError(string format, params object[] args) =>
        LogError(format, args);
}
