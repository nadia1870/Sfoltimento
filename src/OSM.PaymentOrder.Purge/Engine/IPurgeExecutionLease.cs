namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Possesso del lock di istanza. Va liberato con await using, e verificato
/// nei punti in cui il run puo' fermarsi senza danno.
///
/// La verifica non e' gratuita — e' un giro sul database — quindi non va messa
/// fra una slice e l'altra, dove costerebbe quanto il pacing. Il punto giusto
/// e' fra una strategia e l'altra: e' li' che un run puo' interrompersi
/// lasciando tutto coerente, ed e' abbastanza frequente da accorgersi di una
/// connessione caduta prima che il danno diventi grande.
/// </summary>
public interface IPurgeExecutionLease : IAsyncDisposable
{
    /// <summary>
    /// Solleva se il lock non e' piu' garantito: connessione chiusa, o
    /// APPLOCK_MODE che non riporta piu' un lock esclusivo.
    /// </summary>
    Task EnsureHeldAsync(CancellationToken ct);
}
