namespace Foodplanner.Infrastructure.Import.Ai;

/// <summary>
/// The AI fallback for recipe import (T-077, Q-028). Off by default everywhere: <see cref="Enabled"/> is false here and in
/// appsettings.json, and is switched on per environment through configuration only (user secrets locally, a Key Vault reference in
/// test). <see cref="ApiKey"/> is never in the repo. Without a key the feature behaves as if it were off.
/// </summary>
public sealed class AiImportOptions
{
    public const string Section = "AiImport";

    public bool Enabled { get; set; }

    /// <summary>The Anthropic API key. Secret: user secrets or Key Vault only.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The model id, so it can change without code (Q-028: Claude Haiku 5.5).</summary>
    public string Model { get; set; } = "claude-haiku-5-5";

    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    public int TimeoutSeconds { get; set; } = 20;

    public int MaxOutputTokens { get; set; } = 2048;

    public int DailyCallsPerHousehold { get; set; } = 20;

    public int MonthlyCallsPerHousehold { get; set; } = 200;

    /// <summary>The cap on calls by all households together in one UTC month.</summary>
    public int GlobalMonthlyCalls { get; set; } = 2000;

    /// <summary>On and with a key: the only state in which a call may leave the server.</summary>
    public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(ApiKey);
}
