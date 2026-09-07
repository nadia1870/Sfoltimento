using System.Reflection;
using Microsoft.Data.SqlClient;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// Costruisce una SqlException con un numero d'errore scelto.
///
/// Serve perche' SqlException non ha costruttori pubblici e i numeri che ci
/// interessano — 1205 deadlock, 10054 connessione azzerata — non si provocano
/// a comando su un database di prova. Senza questo, la classificazione di D-7
/// resterebbe verificabile solo sulla carta.
///
/// E' l'unico punto della suite che usa la riflessione su tipi non pubblici, ed
/// e' anche l'unico che puo' rompersi aggiornando Microsoft.Data.SqlClient. Per
/// questo i costruttori vengono cercati per forma invece che per firma esatta,
/// e il fallimento e' esplicito: meglio un messaggio che dice cosa e' cambiato
/// che un test verde per la ragione sbagliata.
///
/// Alternativa scartata: provocare l'eccezione vera aprendo una connessione a
/// un database inesistente. Funziona solo per il 4060 e richiede un server.
/// </summary>
internal static class SqlExceptionFactory
{
    public static Task<SqlException> WithNumberAsync(int number) =>
        Task.FromResult(Create(number));

    public static SqlException Create(int number, string message = "guasto simulato")
    {
        var errore = CreateError(number, message);

        var collezione = (SqlErrorCollection)Activator.CreateInstance(
            typeof(SqlErrorCollection), nonPublic: true)!;

        typeof(SqlErrorCollection)
            .GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(collezione, [errore]);

        var crea = typeof(SqlException).GetMethod(
            "CreateException",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            [typeof(SqlErrorCollection), typeof(string)],
            null)
            ?? throw new InvalidOperationException(
                "SqlException.CreateException(SqlErrorCollection, string) non trovato: " +
                "Microsoft.Data.SqlClient e' cambiato e questo helper va aggiornato.");

        return (SqlException)crea.Invoke(null, [collezione, "0.0"])!;
    }

    /// <summary>
    /// Il costruttore di SqlError cambia numero di parametri fra le versioni.
    /// Si prende quello con piu' parametri e si riempie per tipo: il numero
    /// d'errore nel primo int, il messaggio nella stringa piu' lunga
    /// disponibile, il resto ai valori di default.
    /// </summary>
    private static SqlError CreateError(int number, string message)
    {
        var ctor = typeof(SqlError)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Nessun costruttore interno di SqlError: helper da aggiornare.");

        var parametri = ctor.GetParameters();
        var argomenti = new object?[parametri.Length];
        var numeroAssegnato = false;
        var messaggioAssegnato = false;

        for (var i = 0; i < parametri.Length; i++)
        {
            var t = parametri[i].ParameterType;

            if (!numeroAssegnato && t == typeof(int))
            {
                argomenti[i] = number;
                numeroAssegnato = true;
            }
            else if (!messaggioAssegnato && t == typeof(string)
                     && parametri[i].Name?.Contains("essage", StringComparison.OrdinalIgnoreCase) == true)
            {
                argomenti[i] = message;
                messaggioAssegnato = true;
            }
            else if (t == typeof(string))
            {
                argomenti[i] = string.Empty;
            }
            else
            {
                argomenti[i] = t.IsValueType ? Activator.CreateInstance(t) : null;
            }
        }

        if (!numeroAssegnato)
        {
            throw new InvalidOperationException(
                "Il costruttore di SqlError non accetta un int: helper da aggiornare.");
        }

        var errore = (SqlError)ctor.Invoke(argomenti);

        // Verifica immediata: se la riflessione ha riempito i parametri
        // nell'ordine sbagliato, il test deve fallire qui e non piu' avanti con
        // un esito inspiegabile.
        if (errore.Number != number)
        {
            throw new InvalidOperationException(
                $"SqlError costruito con Number={errore.Number} invece di {number}: " +
                "l'ordine dei parametri e' cambiato, helper da aggiornare.");
        }

        return errore;
    }
}
