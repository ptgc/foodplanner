using Foodplanner.Infrastructure.Export;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foodplanner.Infrastructure.Oda;

public static class OdaRegistration
{
    /// <summary>
    /// Registers the Oda cart integration (T-053). All Oda objects are stateless: no cache, no store. The transport is a
    /// <c>TryAdd</c> so a test host can swap in a fake Oda.
    /// </summary>
    public static IServiceCollection AddOda(this IServiceCollection services)
    {
        services.AddOptions<OdaOptions>().BindConfiguration(OdaOptions.Section);
        services.TryAddSingleton(_ => OdaInnerHandler.CreateDefault());
        services.TryAddSingleton<OdaHttp>();
        services.TryAddSingleton<OdaOAuth>();
        services.TryAddSingleton<OdaMcpSessionFactory>();
        services.TryAddScoped<OdaCartAction>();
        services.TryAddScoped<OdaSyncAction>();
        services.AddShoppingListExporter<OdaShoppingListExporter>();
        return services;
    }
}
