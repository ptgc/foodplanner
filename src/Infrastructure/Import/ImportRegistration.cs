using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foodplanner.Infrastructure.Import;

public static class ImportRegistration
{
    /// <summary>
    /// Registers the SSRF-safe page fetcher with real DNS and the public-internet policy. The resolver and the policy are
    /// <c>TryAdd</c>, so a test host can replace them before this runs or remove and re-add them after; there is deliberately
    /// no configuration setting that widens the policy.
    /// </summary>
    public static IServiceCollection AddRecipeImport(this IServiceCollection services)
    {
        services.AddOptions<RecipeImportOptions>().BindConfiguration(RecipeImportOptions.Section);
        services.TryAddSingleton<IHostResolver, DnsHostResolver>();
        services.TryAddSingleton<IFetchPolicy, PublicInternetPolicy>();
        services.TryAddSingleton<IWebPageFetcher, SafeWebPageFetcher>();
        return services;
    }
}
