using System.Net;

namespace Foodplanner.Api.Tests.Auth;

public sealed class LoginTests(AuthDatabase db) : IClassFixture<AuthDatabase>, IAsyncLifetime
{
    private AuthHost _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = AuthHost.Create(db.ConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Login_logout_round_trip()
    {
        var email = AuthHost.NewEmail();
        var user = await _host.CreateUserAsync(email);
        using var client = _host.CreateClient();

        using var login = await client.LoginAsync(email, AuthHost.Password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.Contains(login.SetCookies(), c => c.StartsWith("__Host-foodplanner=", StringComparison.Ordinal));

        using var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await me.JsonAsync();
        Assert.Equal(user.Id, body.GetProperty("userId").GetGuid());
        Assert.Equal(email, body.GetProperty("email").GetString());

        using var logout = await client.PostAsync("/api/auth/logout");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var meAfter = await client.GetAsync("/api/auth/me");
        await meAfter.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Auth_cookie_flags()
    {
        var email = AuthHost.NewEmail();
        await _host.CreateUserAsync(email);
        using var client = _host.CreateClient();

        using var login = await client.LoginAsync(email, AuthHost.Password);

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.SetCookies(), c => c.StartsWith("__Host-foodplanner=", StringComparison.Ordinal));
        var attributes = cookie.Split(';').Select(a => a.Trim().ToLowerInvariant()).ToList();
        Assert.Contains("httponly", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("samesite=lax", attributes);
        Assert.Contains("path=/", attributes);
        Assert.DoesNotContain(attributes, a => a.StartsWith("domain", StringComparison.Ordinal));
        Assert.Contains(attributes, a => a.StartsWith("expires=", StringComparison.Ordinal)); // persistent, not a session cookie
    }

    [Fact]
    public async Task Antiforgery_cookie_flags()
    {
        using var client = _host.CreateClient();

        using var response = await client.GetAsync("/api/auth/antiforgery");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.SetCookies(), c => c.StartsWith("__Host-foodplanner-xsrf=", StringComparison.Ordinal));
        var attributes = cookie.Split(';').Select(a => a.Trim().ToLowerInvariant()).ToList();
        Assert.Contains("httponly", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("samesite=strict", attributes);
        Assert.Contains("path=/", attributes);
        Assert.DoesNotContain(attributes, a => a.StartsWith("domain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plain_http_hop_behind_tls_termination_still_works()
    {
        // Locally and behind the ingress the API itself sees plain HTTP; the Secure cookies must still be issued.
        using var client = _host.CreateClient(useHttp: true);

        using var response = await client.GetAsync("/api/auth/antiforgery");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.SetCookies(), c => c.StartsWith("__Host-foodplanner-xsrf=", StringComparison.Ordinal) && c.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Trusted_proxy_forwarded_proto_is_used_instead_of_the_https_rewrite()
    {
        await using var proxied = AuthHost.Create(db.ConnectionString, new Dictionary<string, string?> { ["ForwardedHeaders:TrustedNetworks:0"] = "10.0.0.0/8" });
        using var client = proxied.CreateClient(remoteIp: "10.1.2.3", useHttp: true);

        using var response = await client.GetAsync("/api/auth/antiforgery", new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Overlong_login_email_is_a_validation_error()
    {
        using var client = _host.CreateClient();

        using var response = await client.LoginAsync(new string('a', 300) + "@example.com", AuthHost.Password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<(string Type, string Title, string Detail)> Failure(HttpResponseMessage response)
    {
        await response.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");
        var body = await response.JsonAsync();
        return (body.GetProperty("type").GetString()!, body.GetProperty("title").GetString()!, body.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task Login_failures_are_indistinguishable()
    {
        var wrongPasswordEmail = AuthHost.NewEmail();
        var lockedEmail = AuthHost.NewEmail();
        await _host.CreateUserAsync(wrongPasswordEmail);
        await _host.CreateUserAsync(lockedEmail);
        using var client = _host.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            using var attempt = await client.LoginAsync(lockedEmail, "wrong password " + i);
            await attempt.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");
        }

        using var unknown = await client.LoginAsync(AuthHost.NewEmail(), AuthHost.Password);
        using var wrong = await client.LoginAsync(wrongPasswordEmail, "not the password");
        using var locked = await client.LoginAsync(lockedEmail, AuthHost.Password);

        var expected = await Failure(unknown);
        Assert.Equal(expected, await Failure(wrong));
        Assert.Equal(expected, await Failure(locked));
    }

    [Fact]
    public async Task Unknown_email_still_hashes()
    {
        using var client = _host.CreateClient();
        var before = _host.Hasher.Verifications;

        using var response = await client.LoginAsync(AuthHost.NewEmail(), AuthHost.Password);

        await response.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");
        Assert.Equal(1, _host.Hasher.Verifications - before);
    }

    [Fact]
    public async Task Lockout_after_5_failures()
    {
        var email = AuthHost.NewEmail();
        await _host.CreateUserAsync(email);
        using var client = _host.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            using var attempt = await client.LoginAsync(email, "wrong password " + i);
            await attempt.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");
        }

        using var locked = await client.LoginAsync(email, AuthHost.Password);
        await locked.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");

        _host.Time.Now = _host.Time.Now.AddMinutes(15);
        using var unlocked = await client.LoginAsync(email, AuthHost.Password);

        Assert.Equal(HttpStatusCode.NoContent, unlocked.StatusCode);
    }

    [Fact]
    public async Task Four_failures_do_not_lock_and_success_resets_the_counter()
    {
        var email = AuthHost.NewEmail();
        await _host.CreateUserAsync(email);
        using var client = _host.CreateClient();
        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < 4; i++)
            {
                using var attempt = await client.LoginAsync(email, "wrong password " + i);
                await attempt.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");
            }

            using var ok = await client.LoginAsync(email, AuthHost.Password);
            Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        }
    }

    [Fact]
    public async Task Unconfirmed_login_refused()
    {
        var email = AuthHost.NewEmail();
        await _host.CreateUserAsync(email, confirmed: false);
        using var client = _host.CreateClient();

        using var unconfirmed = await client.LoginAsync(email, AuthHost.Password);
        using var wrong = await client.LoginAsync(email, "not the password");

        Assert.Equal(await Failure(wrong), await Failure(unconfirmed));
    }

    [Fact]
    public async Task Protected_endpoint_returns_401_problem_not_redirect()
    {
        using var client = _host.CreateClient();

        using var response = await client.GetAsync("/api/auth/me");

        await response.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("Location"));
    }

    [Fact]
    public async Task Unsafe_requests_need_antiforgery()
    {
        var email = AuthHost.NewEmail();
        await _host.CreateUserAsync(email);
        using var client = _host.CreateClient();
        using var other = _host.CreateClient();
        var foreignToken = await other.GetTokenAsync();
        _ = await client.GetTokenAsync(); // gives this session its own antiforgery cookie

        foreach (var url in new[] { "/api/auth/login", "/api/auth/register" })
        {
            var body = new { email, password = AuthHost.Password }; // register ignores the extra field
            using var noToken = await client.PostWithTokenAsync(url, body, token: null);
            await noToken.AssertProblemAsync(HttpStatusCode.BadRequest, "antiforgery-invalid");
            using var wrongSession = await client.PostWithTokenAsync(url, body, foreignToken);
            await wrongSession.AssertProblemAsync(HttpStatusCode.BadRequest, "antiforgery-invalid");
        }

        // Logout, while signed in (the token must also match the signed-in identity).
        using var login = await client.LoginAsync(email, AuthHost.Password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        using var logoutNoToken = await client.PostWithTokenAsync("/api/auth/logout", null, token: null);
        await logoutNoToken.AssertProblemAsync(HttpStatusCode.BadRequest, "antiforgery-invalid");
        using var logoutWrongSession = await client.PostWithTokenAsync("/api/auth/logout", null, foreignToken);
        await logoutWrongSession.AssertProblemAsync(HttpStatusCode.BadRequest, "antiforgery-invalid");
        using var meStillSignedIn = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, meStillSignedIn.StatusCode);
    }

    [Fact]
    public async Task Token_fetched_before_login_is_refused_after_login()
    {
        var email = AuthHost.NewEmail();
        await _host.CreateUserAsync(email);
        using var client = _host.CreateClient();
        var anonymousToken = await client.GetTokenAsync();
        using var login = await client.PostWithTokenAsync("/api/auth/login", new { email, password = AuthHost.Password }, anonymousToken);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        using var logout = await client.PostWithTokenAsync("/api/auth/logout", null, anonymousToken);

        await logout.AssertProblemAsync(HttpStatusCode.BadRequest, "antiforgery-invalid");
    }
}
