using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Foodplanner.Infrastructure.Import.Ai;

public static class AiImportRegistration
{
    /// <summary>
    /// Registers the AI fallback (T-077). The options default to off. The client is a TryAdd so a test host can replace
    /// <see cref="IRecipeLineAi"/> with a fake. One long-lived <see cref="HttpClient"/> (pooled connections, no per-request
    /// handler churn) talks only to the configured Anthropic base URL; the timeout is applied per call by the assistant.
    /// Redirects are never followed, so the <c>x-api-key</c> header can not be re-sent to another host.
    /// </summary>
    public static IServiceCollection AddAiImport(this IServiceCollection services)
    {
        services.AddOptions<AiImportOptions>().BindConfiguration(AiImportOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AiImportOptions>, AiImportOptionsValidator>();
        services.TryAddSingleton<IRecipeLineAi>(sp => new AnthropicRecipeLineAi(
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            },
            sp.GetRequiredService<IOptions<AiImportOptions>>(),
            sp.GetService<ILogger<AnthropicRecipeLineAi>>()));
        services.AddScoped<IAiUsageLimiter, AiUsageLimiter>();
        services.AddScoped<IAiImportAssistant, AiImportAssistant>();
        return services;
    }
}

/// <summary>
/// Outside Development and Testing an active AI fallback must use an https <see cref="AiImportOptions.BaseUrl"/>: the API key goes in
/// a header, so plain http would send it in the clear. The host fails to start with a message that names the setting.
/// </summary>
public sealed class AiImportOptionsValidator(IHostEnvironment environment) : IValidateOptions<AiImportOptions>
{
    public ValidateOptionsResult Validate(string? name, AiImportOptions options)
    {
        if (!options.IsActive || environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return ValidateOptionsResult.Success;
        }

        return Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{AiImportOptions.Section}:BaseUrl must be an absolute https URL outside the Development and Testing environments, "
                + "because the API key is sent to it. Fix the setting or turn AiImport:Enabled off.");
    }
}
