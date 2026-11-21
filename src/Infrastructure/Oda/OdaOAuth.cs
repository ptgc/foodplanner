using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Foodplanner.Domain.ShoppingLists.Cart;
using Microsoft.Extensions.Options;

namespace Foodplanner.Infrastructure.Oda;

/// <summary>Oda's OAuth endpoints, read from its published metadata for one action. Every URL is on the allow-list.</summary>
public sealed record OdaEndpoints(Uri Token, Uri Revocation, string Resource);

/// <summary>
/// OAuth 2.0 authorization code with PKCE S256 for a public client (token endpoint auth method <c>none</c>, no client secret
/// anywhere), plus token revocation (T-053). Discovery follows the protected-resource metadata, then the authorization-server
/// metadata (saved as fixtures from spikes/oda/findings). Nothing is cached: every action discovers again, so no instance keeps state.
/// </summary>
public sealed class OdaOAuth(OdaHttp http, IOptions<OdaOptions> options)
{
    private OdaOptions Settings => options.Value;

    // ---- PKCE and authorize URL (pure; used when a flow starts, which calls nothing) ----------------------------------

    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The URL the browser is sent to. Null when Oda is not configured or the configured authorize URL is not allowed.</summary>
    public Uri? BuildAuthorizeUrl(string state, string verifier)
    {
        if (!Settings.IsConfigured || !Uri.TryCreate(Settings.AuthorizeUrl, UriKind.Absolute, out var authorize) || !Settings.IsAllowed(authorize))
        {
            return null;
        }

        var query = string.Join(
            "&",
            new (string Key, string Value)[]
            {
                ("response_type", "code"),
                ("client_id", Settings.ClientId!.Trim()),
                ("redirect_uri", Settings.RedirectUri!.AbsoluteUri),
                ("scope", "mcp"),
                ("state", state),
                ("code_challenge", Challenge(verifier)),
                ("code_challenge_method", "S256"),
                ("resource", Settings.McpUrl),
            }.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return new UriBuilder(authorize) { Query = query }.Uri;
    }

    // ---- Discovery ---------------------------------------------------------------------------------------------------

    public async Task<OdaEndpoints> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(Settings.McpUrl, UriKind.Absolute, out var mcp) || !Settings.IsAllowed(mcp))
        {
            throw new ProviderUnavailableException("The Oda server address is not allowed.");
        }

        var prmUrl = new Uri($"{mcp.Scheme}://{mcp.Authority}/.well-known/oauth-protected-resource{mcp.AbsolutePath}");
        using var prm = await GetJsonAsync(prmUrl, cancellationToken);
        var resource = Text(prm.RootElement, "resource");
        var servers = prm.RootElement.TryGetProperty("authorization_servers", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
            : [];
        if (resource is null || servers.Length == 0 || !Uri.TryCreate(servers[0].TrimEnd('/'), UriKind.Absolute, out var issuer) || !Settings.IsAllowed(issuer))
        {
            throw new ProviderUnavailableException("Oda's resource metadata is not as expected.");
        }

        var metaUrl = new Uri($"{issuer.Scheme}://{issuer.Authority}/.well-known/oauth-authorization-server{issuer.AbsolutePath}");
        using var meta = await GetJsonAsync(metaUrl, cancellationToken);
        var root = meta.RootElement;
        if (root.TryGetProperty("code_challenge_methods_supported", out var methods)
            && methods.ValueKind == JsonValueKind.Array
            && !methods.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.String && m.GetString() == "S256"))
        {
            throw new ProviderUnavailableException("Oda does not offer PKCE S256.");
        }

        return new OdaEndpoints(AllowedUrl(root, "token_endpoint"), AllowedUrl(root, "revocation_endpoint"), resource);
    }

    private Uri AllowedUrl(JsonElement root, string name) =>
        Uri.TryCreate(Text(root, name), UriKind.Absolute, out var uri) && Settings.IsAllowed(uri)
            ? uri
            : throw new ProviderUnavailableException("Oda's authorization metadata is not as expected.");

    private async Task<JsonDocument> GetJsonAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccess)
        {
            throw new ProviderUnavailableException("Oda's metadata could not be read.");
        }

        try
        {
            var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
        }
        catch (JsonException)
        {
        }

        throw new ProviderUnavailableException("Oda's metadata is not as expected.");
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    // ---- Token exchange and revocation -------------------------------------------------------------------------------

    /// <summary>
    /// Exchanges the single-use code. A public client: the form carries the client id and the PKCE verifier, never a client secret
    /// or an Authorization header.
    /// </summary>
    public async Task<OdaTokens> ExchangeAsync(OdaEndpoints endpoints, OdaSecret code, OdaSecret verifier, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.Token)
        {
            Content = new FormUrlEncodedContent(
            [
                new("grant_type", "authorization_code"),
                new("code", code.Reveal()),
                new("redirect_uri", Settings.RedirectUri!.AbsoluteUri),
                new("client_id", Settings.ClientId!.Trim()),
                new("code_verifier", verifier.Reveal()),
                new("resource", endpoints.Resource),
            ]),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccess)
        {
            throw new ProviderUnavailableException("Oda refused the sign-in.");
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var access = Text(document.RootElement, "access_token");
            if (document.RootElement.ValueKind != JsonValueKind.Object || access is null)
            {
                throw new ProviderUnavailableException("Oda's sign-in answer is not as expected.");
            }

            var refresh = Text(document.RootElement, "refresh_token");
            return new OdaTokens(new OdaSecret(access), refresh is null ? null : new OdaSecret(refresh));
        }
        catch (JsonException)
        {
            throw new ProviderUnavailableException("Oda's sign-in answer is not as expected.");
        }
    }

    /// <summary>
    /// Revokes the access token and, when one was issued, the refresh token. Each call is tried twice; true only when every
    /// revoke succeeded. Never throws: it runs in a finally path and must not hide the real result.
    /// </summary>
    public async Task<bool> RevokeAsync(OdaEndpoints endpoints, OdaTokens tokens)
    {
        var closed = await RevokeOneAsync(endpoints, tokens.Access, "access_token");
        if (tokens.Refresh is not null)
        {
            closed &= await RevokeOneAsync(endpoints, tokens.Refresh, "refresh_token");
        }

        return closed;
    }

    private async Task<bool> RevokeOneAsync(OdaEndpoints endpoints, OdaSecret token, string hint)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.Revocation)
                {
                    Content = new FormUrlEncodedContent(
                    [
                        new("token", token.Reveal()),
                        new("token_type_hint", hint),
                        new("client_id", Settings.ClientId!.Trim()),
                    ]),
                };
                // Not the request's token: revoking must still happen when the request was cancelled. Its own timeout bounds it.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, Settings.RevokeTimeoutSeconds)));
                if ((await http.SendAsync(request, timeout.Token)).IsSuccess)
                {
                    return true;
                }
            }
            catch (ProviderUnavailableException)
            {
            }
            catch (OperationCanceledException)
            {
                // The revoke's own timeout elapsed; try again.
            }
        }

        return false;
    }
}
