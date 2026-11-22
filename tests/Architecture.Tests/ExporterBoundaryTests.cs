using Foodplanner.Domain.ShoppingLists.Export;

namespace Foodplanner.Architecture.Tests;

/// <summary>T-050, ADR 0008: the exporter port is provider-neutral and its implementations live behind the adapter boundary.</summary>
public class ExporterBoundaryTests
{
    private static readonly string[] ProviderWords = ["Oda", "Kolonial", "Rema", "Kiwi", "Meny", "Mcp"];

    [Fact]
    public void The_exporter_port_lives_in_Domain_and_every_implementation_lives_outside_it()
    {
        var domain = typeof(Foodplanner.Domain.AssemblyMarker).Assembly;
        Assert.Equal(domain, typeof(IShoppingListExporter).Assembly);

        var implementationsInDomain = domain.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IShoppingListExporter).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToArray();

        Assert.Empty(implementationsInDomain);
        Assert.Contains(
            typeof(Foodplanner.Infrastructure.AssemblyMarker).Assembly.GetTypes(),
            t => typeof(IShoppingListExporter).IsAssignableFrom(t) && t is { IsClass: true });
    }

    [Fact]
    public void The_oda_exporter_lives_in_Infrastructure_and_is_covered_by_the_boundary()
    {
        var oda = typeof(Foodplanner.Infrastructure.Oda.OdaShoppingListExporter);
        Assert.True(typeof(IShoppingListExporter).IsAssignableFrom(oda));
        Assert.Equal(typeof(Foodplanner.Infrastructure.AssemblyMarker).Assembly, oda.Assembly);
        Assert.Equal("oda", new Foodplanner.Infrastructure.Oda.OdaShoppingListExporter().Name);
    }

    [Fact]
    public void The_Oda_code_keeps_no_static_state_so_nothing_from_Oda_outlives_a_request()
    {
        // T-053 "No_data_kept": no static collection or mutable static field anywhere in the Oda namespace (compiler-generated lambda caches excluded).
        var offenders = typeof(Foodplanner.Infrastructure.AssemblyMarker).Assembly.GetTypes()
            .Where(t => t.Namespace == "Foodplanner.Infrastructure.Oda" && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            .SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(f => !f.IsLiteral && (!f.IsInitOnly || (f.FieldType != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(f.FieldType))))
                .Select(f => $"{t.FullName}.{f.Name}"))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_Oda_type_holds_a_collection_in_an_instance_field_so_purchase_history_cannot_outlive_a_request()
    {
        // T-114 "History_never_persisted_or_logged": the "usually bought" ids live in one local variable of the fill. Options classes
        // (configuration) and the per-request outcome record (the lines for the response, search product names only) may hold lists.
        var offenders = typeof(Foodplanner.Infrastructure.AssemblyMarker).Assembly.GetTypes()
            .Where(t => t.Namespace == "Foodplanner.Infrastructure.Oda" && t.Name is not (nameof(Foodplanner.Infrastructure.Oda.OdaOptions) or nameof(Foodplanner.Infrastructure.Oda.OdaActionOutcome))
                        && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            .SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(f => f.FieldType != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(f.FieldType))
                .Select(f => $"{t.FullName}.{f.Name}"))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Domain_has_no_provider_types_in_the_export_model_or_anywhere()
    {
        var offenders = typeof(Foodplanner.Domain.AssemblyMarker).Assembly.GetTypes()
            .Where(t => ProviderWords.Any(w => t.Name.StartsWith(w, StringComparison.Ordinal)
                                               || (t.Namespace ?? string.Empty).Split('.').Contains(w, StringComparer.Ordinal)))
            .Select(t => t.FullName)
            .ToArray();

        Assert.Empty(offenders);
    }
}
