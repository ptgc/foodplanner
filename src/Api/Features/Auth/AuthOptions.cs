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

    /// <summary>
    /// How long a set-password link works, and also the age after which an account that never set its password is
    /// removed (T-072), so a link never outlives its account.
    /// </summary>
    public int ConfirmationTokenLifetimeHours { get; set; } = 24;

    /// <summary>
    /// A repeat registration of an unconfirmed address sends a new link (and invalidates the old one) at most this
    /// often per address; in between it answers the same 202 and does nothing (T-072).
    /// </summary>
    public int ResendCooldownMinutes { get; set; } = 5;

    /// <summary>How often the cleanup job looks for expired unconfirmed accounts (T-072).</summary>
    public int UnconfirmedCleanupIntervalMinutes { get; set; } = 15;

    /// <summary>Most accounts one cleanup batch deletes (T-072).</summary>
    public int UnconfirmedCleanupBatchSize { get; set; } = 100;
}
