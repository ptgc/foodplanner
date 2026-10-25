using Foodplanner.Domain.ShoppingLists.Export;

namespace Foodplanner.Architecture.Tests;

/// <summary>T-050, ADR 0008: the exporter port is provider-neutral and its implementations live behind the adapter boundary.</summary>
public class ExporterBoundaryTests
{
    private static readonly string[] ProviderWords = ["Oda", "Kolonial", "Rema", "Kiwi", "Meny"];

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
