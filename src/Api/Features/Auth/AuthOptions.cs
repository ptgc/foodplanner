namespace Foodplanner.Api.Features.Auth;

/// <summary>Section <c>Auth</c>. Every value is configurable; the defaults are the ones in the T-009 spec.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// Origin of the web app, used for links in emails. Configuration only, never the request Host header, which a
    /// caller controls. Deployed environments must set it.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:3000";

    public int MinPasswordLength { get; set; } = 10;

    public int LockoutMaxFailedAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;

    public int CookieLifetimeDays { get; set; } = 14;

    public int ConfirmationTokenLifetimeHours { get; set; } = 24;
}
