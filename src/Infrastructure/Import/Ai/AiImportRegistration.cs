using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Foodplanner.Infrastructure.Import.Ai;

public static class AiImportRegistration
{
    /// <summary>
    /// Registers the AI fallback (T-077). The options default to off. The client is a TryAdd so a test host can replace
    /// <see cref="IRecipeLineAi"/> with a fake. One long-lived <see cref="HttpClient"/> (pooled connections, no per-request
    /// handler churn) talks only to the configured Anthropic base URL; the timeout is applied per call by the assistant.
    /// </summary>
    public static IServiceCollection AddAiImport(this IServiceCollection services)
    {
        services.AddOptions<AiImportOptions>().BindConfiguration(AiImportOptions.Section);
        services.TryAddSingleton<IRecipeLineAi>(sp => new AnthropicRecipeLineAi(
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            },
            sp.GetRequiredService<IOptions<AiImportOptions>>()));
        services.AddScoped<IAiUsageLimiter, AiUsageLimiter>();
        services.AddScoped<IAiImportAssistant, AiImportAssistant>();
        return services;
    }
}
