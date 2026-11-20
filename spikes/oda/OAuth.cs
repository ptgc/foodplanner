using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OdaSpike;

/// <summary>MCP authorization: protected-resource metadata, authorization-server metadata, dynamic client registration, authorization code + PKCE (S256), refresh.</summary>
internal static class OAuth
{
    public const string McpUrl = "https://oda.com/mcp";
    private static readonly HttpClient Http = new();

    private sealed record AsMeta(string Authorization, string Token, string? Registration, string Resource);

    private static async Task<AsMeta> DiscoverAsync()
    {
        var prm = JsonNode.Parse(await Http.GetStringAsync("https://oda.com/.well-known/oauth-protected-resource/mcp"))!;
        var issuer = prm["authorization_servers"]![0]!.GetValue<string>().TrimEnd('/');
        var uri = new Uri(issuer);
        var meta = JsonNode.Parse(await Http.GetStringAsync($"{uri.Scheme}://{uri.Authority}/.well-known/oauth-authorization-server{uri.AbsolutePath}"))!;
        return new AsMeta(
            meta["authorization_endpoint"]!.GetValue<string>(),
            meta["token_endpoint"]!.GetValue<string>(),
            meta["registration_endpoint"]?.GetValue<string>(),
            prm["resource"]!.GetValue<string>());
    }

    public static async Task LoginAsync()
    {
        var meta = await DiscoverAsync();
        if (meta.Registration is null) { throw new InvalidOperationException("Server offers no dynamic client registration."); }

        using var listener = new HttpListener();
        var port = FreePort();
        var redirect = $"http://127.0.0.1:{port}/callback";
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        // Dynamic client registration (public client, no secret).
        var reg = new JsonObject
        {
            ["client_name"] = "foodplanner-oda-spike (T-001, local test)",
            ["redirect_uris"] = new JsonArray(redirect),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none",
            ["scope"] = "mcp",
        };
        var regResp = await Http.PostAsync(meta.Registration, new StringContent(reg.ToJsonString(), Encoding.UTF8, "application/json"));
        var regBody = await regResp.Content.ReadAsStringAsync();
        Console.WriteLine($"DCR: HTTP {(int)regResp.StatusCode}");
        if (!regResp.IsSuccessStatusCode) { Console.WriteLine(regBody); throw new InvalidOperationException("Registration failed."); }
        var regJson = JsonNode.Parse(regBody)!;
        var clientId = regJson["client_id"]!.GetValue<string>();
        var clientSecret = regJson["client_secret"]?.GetValue<string>();
        Console.WriteLine($"DCR: client_secret issued: {clientSecret is not null}; token_endpoint_auth_method: {regJson["token_endpoint_auth_method"]}; redirect_uris: {regJson["redirect_uris"]?.ToJsonString()}");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var authUrl = $"{meta.Authorization}?response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirect)}" +
                      $"&scope=mcp&state={state}&code_challenge={challenge}&code_challenge_method=S256&resource={Uri.EscapeDataString(meta.Resource)}";
        Console.WriteLine("Opening the browser to sign in to Oda. If it does not open, paste this URL:");
        Console.WriteLine(authUrl);
        try { Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true }); } catch (Exception e) { Console.WriteLine($"(could not open browser: {e.Message})"); }

        var ctxTask = listener.GetContextAsync();
        if (await Task.WhenAny(ctxTask, Task.Delay(TimeSpan.FromMinutes(5))) != ctxTask) { throw new TimeoutException("No sign-in callback within 5 minutes."); }
        var ctx = await ctxTask;
        var query = ctx.Request.QueryString;
        var ok = query["code"] is not null && query["state"] == state;
        var html = Encoding.UTF8.GetBytes(ok ? "<h3>Signed in. You can close this tab and go back to the terminal.</h3>" : "<h3>Sign-in failed. See the terminal.</h3>");
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.OutputStream.WriteAsync(html);
        ctx.Response.Close();
        if (!ok) { throw new InvalidOperationException($"Authorization failed: error={query["error"]} description={query["error_description"]}"); }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = query["code"]!,
            ["redirect_uri"] = redirect,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
            ["resource"] = meta.Resource,
        };
        if (clientSecret is not null) { form["client_secret"] = clientSecret; }
        SaveToken(await PostTokenAsync(meta.Token, form), clientId, clientSecret);
    }

    public static async Task<string> GetAccessTokenAsync()
    {
        var access = Secrets.Get("Oda:AccessToken");
        var expires = Secrets.Get("Oda:ExpiresAtUtc");
        if (access is not null && expires is not null && DateTimeOffset.Parse(expires) > DateTimeOffset.UtcNow.AddMinutes(1)) { return access; }

        var refresh = Secrets.Get("Oda:RefreshToken");
        var clientId = Secrets.Get("Oda:ClientId");
        if (refresh is null || clientId is null) { throw new InvalidOperationException("Not signed in (or no refresh token). Run: login"); }
        Console.WriteLine("Access token expired; refreshing.");
        var meta = await DiscoverAsync();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = clientId,
            ["resource"] = meta.Resource,
        };
        var secret = Secrets.Get("Oda:ClientSecret");
        if (secret is not null) { form["client_secret"] = secret; }
        SaveToken(await PostTokenAsync(meta.Token, form), clientId, secret);
        return Secrets.Get("Oda:AccessToken")!;
    }

    private static async Task<JsonNode> PostTokenAsync(string tokenEndpoint, Dictionary<string, string> form)
    {
        var resp = await Http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form));
        var body = await resp.Content.ReadAsStringAsync();
        Console.WriteLine($"Token endpoint: HTTP {(int)resp.StatusCode}");
        if (!resp.IsSuccessStatusCode) { throw new InvalidOperationException("Token request failed: " + (JsonNode.Parse(body)?["error"] ?? "unknown")); }
        return JsonNode.Parse(body)!;
    }

    private static void SaveToken(JsonNode token, string clientId, string? clientSecret)
    {
        var expiresIn = token["expires_in"]?.GetValue<int>() ?? 3600;
        var newRefresh = token["refresh_token"]?.GetValue<string>() ?? Secrets.Get("Oda:RefreshToken");
        Secrets.Set(
            ("Oda:ClientId", clientId),
            ("Oda:ClientSecret", clientSecret),
            ("Oda:AccessToken", token["access_token"]!.GetValue<string>()),
            ("Oda:RefreshToken", newRefresh),
            ("Oda:ExpiresAtUtc", DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToString("O")));
        // Only non-secret metadata is printed: it documents token lifetime for the ADR.
        Console.WriteLine($"Token saved to user secrets. type={token["token_type"]} scope={token["scope"]} expires_in={expiresIn}s refresh_token_present={newRefresh is not null} refresh_rotated={token["refresh_token"] is not null}");
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
