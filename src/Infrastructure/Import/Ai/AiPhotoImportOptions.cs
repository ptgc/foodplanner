using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Foodplanner.Infrastructure.Import.Ai;

/// <summary>
/// Recipe from a photo (T-118, Q-034). Off by default everywhere: it is switched on through configuration outside the repo only.
/// It uses the shared app key <see cref="AiImportOptions.ApiKey"/> and base URL <see cref="AiImportOptions.BaseUrl"/> (one key,
/// Key Vault or user secrets, never in the repo); without a key the feature behaves as if it were off. None of these values is secret.
/// </summary>
public sealed class AiPhotoImportOptions
{
    public const string Section = "AiPhotoImport";

    public bool Enabled { get; set; }

    /// <summary>The model id; config, so the eval result can change it without code.</summary>
    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>The value of <c>output_config.effort</c> (low, medium, high, ...). Thinking cannot be switched off, depth is the effort.</summary>
    public string Effort { get; set; } = "medium";

    /// <summary>Thinking counts toward it.</summary>
    public int MaxOutputTokens { get; set; } = 12_000;

    public int TimeoutSeconds { get; set; } = 90;

    public int MaxPhotos { get; set; } = 4;

    public int DailyPerHousehold { get; set; } = 2;

    public int GlobalMonthly { get; set; } = 300;

    /// <summary>Calls to Claude running at once in this process.</summary>
    public int MaxConcurrent { get; set; } = 3;

    /// <summary>On and with a key: the only state in which a photo may leave the server.</summary>
    public bool IsActive(AiImportOptions shared) => Enabled && !string.IsNullOrWhiteSpace(shared.ApiKey);
}

/// <summary>Outside Development and Testing an active photo import must use an https base URL (the key goes in a header).</summary>
public sealed class AiPhotoImportOptionsValidator(IHostEnvironment environment, IOptions<AiImportOptions> shared) : IValidateOptions<AiPhotoImportOptions>
{
    public ValidateOptionsResult Validate(string? name, AiPhotoImportOptions options)
    {
        if (!options.IsActive(shared.Value) || environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return ValidateOptionsResult.Success;
        }

        return Uri.TryCreate(shared.Value.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{AiImportOptions.Section}:BaseUrl must be an absolute https URL outside the Development and Testing environments, "
                + "because the API key is sent to it. Fix the setting or turn AiPhotoImport:Enabled off.");
    }
}
