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

    /// <summary>
    /// D-11: gli errori di integrita' sono divisibili, e sono un insieme
    /// disgiunto dai transitori. Un 547 non passa riprovando; un deadlock
    /// non si isola dividendo.
    /// </summary>
    [Theory]
    [InlineData(547)]
    [InlineData(2627)]
    [InlineData(2601)]
    public void Data_integrity_errors_are_splittable_and_not_transient(int number)
    {
        Assert.True(SqlErrors.IsDataIntegrity(number));
        Assert.False(SqlErrors.IsTransient(number));
        Assert.False(SqlErrors.IsConcurrency(number));
    }

    [Theory]
    [InlineData(1205)]   // deadlock
    [InlineData(10054)]  // connessione azzerata
    [InlineData(8134)]   // divisione per zero
    [InlineData(208)]    // oggetto inesistente: un difetto, non un dato
    public void Everything_else_is_not_splittable(int number)
    {
        Assert.False(SqlErrors.IsDataIntegrity(number));
    }
}
