using System.Reflection;
using Xunit;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// La suite gira in due job separati, filtrati per categoria: Unit senza
/// database, Integration con SQL Server.
///
/// Il rischio del filtro esplicito e' che una classe nuova non venga marcata:
/// non comparirebbe in nessuno dei due job, e la pipeline resterebbe verde
/// ignorandola. E' il modo peggiore di perdere copertura, perche' non lascia
/// traccia. Questo test lo impedisce.
///
/// La seconda verifica e' altrettanto importante: una classe marcata Unit che
/// usa la fixture del database fallirebbe nel job senza SQL Server, ma solo
/// dopo che qualcuno l'ha scritta e messa in coda. Meglio saperlo qui.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TestCategoryTests
{
    private static IEnumerable<Type> ClassiDiTest =>
        typeof(TestCategoryTests).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetMethods()
                .Any(m => m.GetCustomAttributes<FactAttribute>().Any()
                       || m.GetCustomAttributes<TheoryAttribute>().Any()));

    /// <summary>
    /// I valori si leggono dai metadati e non dall'attributo istanziato:
    /// TraitAttribute e CollectionAttribute prendono i loro argomenti nel
    /// costruttore e non li espongono come proprieta'. xUnit li recupera nello
    /// stesso modo, in fase di scoperta.
    /// </summary>
    private static string? ArgomentoDi(Type tipo, Type attributo, int posizione,
                                       string? primoArgomentoAtteso = null) =>
        tipo.GetCustomAttributesData()
            .Where(a => a.AttributeType == attributo)
            .Where(a => a.ConstructorArguments.Count > posizione)
            .Where(a => primoArgomentoAtteso is null
                        || (string?)a.ConstructorArguments[0].Value == primoArgomentoAtteso)
            .Select(a => (string?)a.ConstructorArguments[posizione].Value)
            .FirstOrDefault();

    private static string? CategoriaDi(Type t) =>
        ArgomentoDi(t, typeof(TraitAttribute), 1, "Category");

    [Fact]
    public void Ogni_classe_di_test_dichiara_una_categoria()
    {
        var senzaCategoria = ClassiDiTest
            .Where(t => CategoriaDi(t) is null)
            .Select(t => t.Name)
            .ToList();

        Assert.True(senzaCategoria.Count == 0,
            "Classi senza [Trait(\"Category\", ...)], non girerebbero in nessun job: " +
            string.Join(", ", senzaCategoria));
    }

    [Fact]
    public void Le_categorie_dichiarate_sono_solo_Unit_o_Integration()
    {
        var sconosciute = ClassiDiTest
            .Select(t => (t.Name, Categoria: CategoriaDi(t)))
            .Where(x => x.Categoria is not null and not "Unit" and not "Integration")
            .Select(x => $"{x.Name} => {x.Categoria}")
            .ToList();

        Assert.True(sconosciute.Count == 0,
            "Categorie non previste dalla pipeline: " + string.Join(", ", sconosciute));
    }

    /// <summary>
    /// Una classe Unit non deve dipendere dalla fixture del database, ne'
    /// direttamente ne' tramite la collection.
    /// </summary>
    [Fact]
    public void Le_classi_Unit_non_usano_la_fixture_del_database()
    {
        var sospette = ClassiDiTest
            .Where(t => CategoriaDi(t) == "Unit")
            .Where(t => ArgomentoDi(t, typeof(CollectionAttribute), 0) == "PurgeDatabase"
                     || t.GetConstructors()
                            .SelectMany(c => c.GetParameters())
                            .Any(p => p.ParameterType == typeof(PurgeDatabaseFixture)))
            .Select(t => t.Name)
            .ToList();

        Assert.True(sospette.Count == 0,
            "Classi marcate Unit che dipendono dal database: " + string.Join(", ", sospette));
    }
}
