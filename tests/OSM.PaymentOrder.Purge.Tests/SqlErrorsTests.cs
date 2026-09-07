using OSM.PaymentOrder.Purge.Engine;
using Xunit;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// La differenza fra contesa e guasto decide due cose diverse: se riprovare la
/// slice, e se il run resta riprendibile. Confonderle costa in entrambi i versi,
/// e la prima versione le confondeva.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SqlErrorsTests
{
    [Theory]
    [InlineData(1205)]  // deadlock
    [InlineData(1222)]  // lock request timeout
    [InlineData(-2)]    // command timeout
    public void Concurrency_errors_retry_the_slice(int number)
    {
        Assert.True(SqlErrors.IsConcurrency(number));
        Assert.True(SqlErrors.IsTransient(number));
    }

    /// <summary>
    /// Una connessione caduta non deve far riprovare la slice: i tentativi si
    /// esaurirebbero e la slice verrebbe abbandonata, lasciando aggregati a
    /// database per un guasto che sarebbe passato da solo.
    /// </summary>
    [Theory]
    [InlineData(233)]    // connessione chiusa dal server
    [InlineData(10054)]  // connessione azzerata dal peer
    [InlineData(40613)]  // database non disponibile
    public void Connection_errors_interrupt_the_run_instead(int number)
    {
        Assert.False(SqlErrors.IsConcurrency(number));
        Assert.True(SqlErrors.IsTransient(number));
    }

    [Theory]
    [InlineData(547)]    // violazione di vincolo: e' un difetto
    [InlineData(2627)]   // chiave duplicata
    [InlineData(8134)]   // divisione per zero
    public void Logical_errors_are_neither(int number)
    {
        Assert.False(SqlErrors.IsConcurrency(number));
        Assert.False(SqlErrors.IsTransient(number));
    }
}
