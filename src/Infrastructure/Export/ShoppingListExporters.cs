using Foodplanner.Domain.ShoppingLists.Export;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foodplanner.Infrastructure.Export;

/// <summary>Finds a registered <see cref="IShoppingListExporter"/> by name (case-insensitive). T-053 adds its own with <c>AddShoppingListExporter</c>.</summary>
public interface IShoppingListExporterSelector
{
    IShoppingListExporter? Find(string? name);
}

public sealed class ShoppingListExporterSelector(IEnumerable<IShoppingListExporter> exporters) : IShoppingListExporterSelector
{
    private readonly IReadOnlyList<IShoppingListExporter> _exporters = [.. exporters];

    public IShoppingListExporter? Find(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : _exporters.FirstOrDefault(e => string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class ShoppingListExportRegistration
{
    public static IServiceCollection AddShoppingListExporters(this IServiceCollection services)
    {
        services.AddShoppingListExporter<TextShoppingListExporter>();
        services.TryAddScoped<IShoppingListExporterSelector, ShoppingListExporterSelector>();
        return services;
    }

    /// <summary>Registers another exporter next to the text one; its <c>Name</c> must be unique.</summary>
    public static IServiceCollection AddShoppingListExporter<T>(this IServiceCollection services)
        where T : class, IShoppingListExporter
    {
        services.AddScoped<IShoppingListExporter, T>();
        return services;
    }
}
