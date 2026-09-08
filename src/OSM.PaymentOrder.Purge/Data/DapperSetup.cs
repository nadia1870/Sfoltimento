using System.Data;
using Dapper;

namespace OSM.PaymentOrder.Purge.Data;

/// <summary>
/// Configurazione globale di Dapper (D-14). Una volta per processo, prima
/// del primo comando: SqlExecutor la invoca dal proprio costruttore statico.
///
/// La mappa DateTime -> DateTime2 e' il punto che conta. Dapper, come
/// AddWithValue, manda un DateTime come SqlDbType.DateTime, il cui minimo e'
/// il 1753: la filigrana della selezione parte da DateTime.MinValue e
/// solleverebbe un'eccezione nel client prima ancora di raggiungere il
/// server. Era il motivo per cui esisteva SqlParam.Typed; con la mappa
/// globale ogni DateTime viaggia come datetime2 anche dove nessuno ci ha
/// pensato. DapperSetupTests lo verifica sul database.
/// </summary>
public static class DapperSetup
{
    private static readonly Lock Gate = new();
    private static bool _done;

    public static void Ensure()
    {
        lock (Gate)
        {
            if (_done) return;

            SqlMapper.AddTypeMap(typeof(DateTime), DbType.DateTime2);
            SqlMapper.AddTypeMap(typeof(DateTime?), DbType.DateTime2);

            _done = true;
        }
    }
}
