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

    /// <summary>Hits asked for per query. Only the first available one is used.</summary>
    public int SearchPageSize { get; set; } = 5;

    /// <summary>Per call to Oda.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    public IReadOnlyList<string> EffectiveAllowedHosts =>
        AllowedHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim().ToLowerInvariant()).ToArray() is { Length: > 0 } hosts
            ? hosts
            : ["oda.com"];

    public Uri? RedirectUri =>
        Uri.TryCreate(WebOrigin?.Trim().TrimEnd('/') + CallbackPath, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : null;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && RedirectUri is not null;

    public bool IsAllowed(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && EffectiveAllowedHosts.Any(h => string.Equals(uri.Host, h, StringComparison.OrdinalIgnoreCase)
                                          || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
}
