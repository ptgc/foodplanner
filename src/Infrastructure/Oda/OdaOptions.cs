namespace Foodplanner.Infrastructure.Oda;

/// <summary>
/// Section <c>Oda</c> (T-053). Nothing here is a secret: the OAuth client is a public client (no client secret exists), and
/// <see cref="ClientId"/> only names the app-wide client registered once per environment (see scripts/oda-register-client).
/// </summary>
public sealed class OdaOptions
{
    public const string Section = "Oda";

    /// <summary>The path of the web app's callback page; the redirect URI registered at Oda is <see cref="WebOrigin"/> plus this.</summary>
    public const string CallbackPath = "/oda/callback";

    /// <summary>The registered client id. Empty means Oda is not configured: the endpoints answer 404 and the client offers only the text export.</summary>
    public string? ClientId { get; set; }

    /// <summary>The public origin of the web app (scheme, host, port; same origin as the SPA and the /api proxy), for example <c>https://app.example.com</c> or <c>http://localhost:3000</c>.</summary>
    public string? WebOrigin { get; set; }

    /// <summary>The MCP server, which is also the OAuth resource.</summary>
    public string McpUrl { get; set; } = "https://oda.com/mcp";

    /// <summary>The authorize endpoint the browser is sent to. It is known up front, so starting a flow makes no call to Oda; it must be on an allowed host.</summary>
    public string AuthorizeUrl { get; set; } = "https://oda.com/o/authorize/";

    /// <summary>Hosts (and their subdomains) that may be called, including every URL taken from discovery metadata. Empty means <c>oda.com</c>.</summary>
    public string[] AllowedHosts { get; set; } = [];

    public int MaxItemsPerAction { get; set; } = 100;

    /// <summary>Hits asked for per query (T-114: the usually bought product is looked for among them, so the page is 10; the tool allows up to 20).</summary>
    public int SearchPageSize { get; set; } = 10;

    /// <summary>Most ingredients one "Sync with Oda" run checks (T-130, Q-039); the rest is offered as "Check the next ones" with an offset.</summary>
    public int MaxSyncIngredients { get; set; } = 200;

    /// <summary>Total time budget of one sync run, checked before every call to Oda (T-130, Q-039).</summary>
    public int SyncBudgetSeconds { get; set; } = 90;

    /// <summary>Most items in one bulk save of Oda choices (T-130).</summary>
    public int MaxBulkPreferences { get; set; } = 200;

    /// <summary>Per call to Oda.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Own time limit for each token revoke, independent of the request's cancellation.</summary>
    public int RevokeTimeoutSeconds { get; set; } = 10;

    public IReadOnlyList<string> EffectiveAllowedHosts =>
        AllowedHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim().ToLowerInvariant()).ToArray() is { Length: > 0 } hosts
            ? hosts
            : ["oda.com"];

    /// <summary>Hosts that may use plain http (local development only).</summary>
    public static bool IsLocalHost(Uri uri) => uri.Host is "localhost" or "127.0.0.1" or "[::1]";

    /// <summary>The return address, or null when the origin is invalid or plain http on a non-local host.</summary>
    public Uri? RedirectUri =>
        Uri.TryCreate(WebOrigin?.Trim().TrimEnd('/') + CallbackPath, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsLocalHost(uri)))
            ? uri
            : null;

    /// <summary>True when a client id and an origin are set but the origin is refused (not https, not a local host), so Oda counts as not configured.</summary>
    public bool HasInsecureOrigin => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(WebOrigin) && RedirectUri is null;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && RedirectUri is not null;

    public bool IsAllowed(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && EffectiveAllowedHosts.Any(h => string.Equals(uri.Host, h, StringComparison.OrdinalIgnoreCase)
                                          || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
}
