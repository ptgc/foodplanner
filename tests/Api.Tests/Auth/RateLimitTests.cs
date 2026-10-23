using System.Net;

namespace Foodplanner.Api.Tests.Auth;

public sealed class RateLimitTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private AuthHost Host(params (string Key, string Value)[] settings) =>
        AuthHost.Create(db.ConnectionString, settings.ToDictionary(s => s.Key, s => (string?)s.Value));

    private static async Task AssertRateLimitedAsync(HttpResponseMessage response)
    {
        await response.AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate-limited");
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values), "Retry-After header missing");
        Assert.True(int.Parse(values!.Single(), System.Globalization.CultureInfo.InvariantCulture) >= 1);
    }

    [Fact]
    public async Task Auth_endpoints_rate_limited_login_by_ip()
    {
        await using var host = Host(("RateLimiting:Auth:PermitLimit", "3"));
        using var client = host.CreateClient("203.0.113.10");
        for (var i = 0; i < 3; i++)
        {
            using var attempt = await client.LoginAsync(AuthHost.NewEmail(), AuthHost.Password);
            await attempt.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");
        }

        using var limited = await client.LoginAsync(AuthHost.NewEmail(), AuthHost.Password);
        await AssertRateLimitedAsync(limited);

        // Another client IP has its own budget.
        using var otherIp = host.CreateClient("203.0.113.11");
        using var allowed = await otherIp.LoginAsync(AuthHost.NewEmail(), AuthHost.Password);
        await allowed.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");
    }

    [Fact]
    public async Task Auth_endpoints_rate_limited_login_by_email_across_ips()
    {
        await using var host = Host(("RateLimiting:LoginByEmail:PermitLimit", "3"));
        var email = AuthHost.NewEmail();
        for (var i = 0; i < 3; i++)
        {
            using var client = host.CreateClient($"198.51.100.{i + 1}");
            using var attempt = await client.LoginAsync(email, AuthHost.Password);
            await attempt.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");
        }

        using var another = host.CreateClient("198.51.100.200");
        using var limited = await another.LoginAsync(email.ToUpperInvariant(), AuthHost.Password); // normalised: case does not matter
        await AssertRateLimitedAsync(limited);
        using var otherEmail = await another.LoginAsync(AuthHost.NewEmail(), AuthHost.Password);
        await otherEmail.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");
    }

    [Fact]
    public async Task Auth_endpoints_rate_limited_register()
    {
        await using var host = Host(("RateLimiting:Auth:PermitLimit", "2"));
        using var client = host.CreateClient("203.0.113.20");
        for (var i = 0; i < 2; i++)
        {
            using var attempt = await client.PostAsync("/api/auth/register", new { email = AuthHost.NewEmail(), password = AuthHost.Password });
            Assert.Equal(HttpStatusCode.Accepted, attempt.StatusCode);
        }

        using var limited = await client.PostAsync("/api/auth/register", new { email = AuthHost.NewEmail(), password = AuthHost.Password });
        await AssertRateLimitedAsync(limited);
    }

    [Fact]
    public async Task Auth_endpoints_rate_limited_confirm_email()
    {
        await using var host = Host(("RateLimiting:Auth:PermitLimit", "2"));
        using var client = host.CreateClient("203.0.113.30");
        for (var i = 0; i < 2; i++)
        {
            using var attempt = await client.PostAsync("/api/auth/confirm-email", new { userId = Guid.NewGuid(), token = "x" });
            await attempt.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
        }

        using var limited = await client.PostAsync("/api/auth/confirm-email", new { userId = Guid.NewGuid(), token = "x" });
        await AssertRateLimitedAsync(limited);
    }

    [Fact]
    public async Task Spoofed_forwarded_header_ignored()
    {
        await using var host = Host(("RateLimiting:Auth:PermitLimit", "2"));
        using var client = host.CreateClient("203.0.113.40");
        for (var i = 1; i <= 2; i++)
        {
            using var attempt = await client.PostAsync(
                "/api/auth/confirm-email",
                new { userId = Guid.NewGuid(), token = "x" },
                new Dictionary<string, string> { ["X-Forwarded-For"] = $"192.0.2.{i}" });
            await attempt.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
        }

        // A third, different X-Forwarded-For from the same untrusted peer is still the same partition.
        using var limited = await client.PostAsync(
            "/api/auth/confirm-email",
            new { userId = Guid.NewGuid(), token = "x" },
            new Dictionary<string, string> { ["X-Forwarded-For"] = "192.0.2.99" });

        await AssertRateLimitedAsync(limited);
    }

    [Fact]
    public async Task Forwarded_header_from_trusted_proxy_network_is_honoured()
    {
        await using var host = Host(("RateLimiting:Auth:PermitLimit", "2"), ("ForwardedHeaders:TrustedNetworks:0", "10.0.0.0/8"));
        using var proxy = host.CreateClient("10.1.2.3");
        for (var i = 1; i <= 4; i++)
        {
            // Four different real clients behind the proxy: each has its own budget.
            using var attempt = await proxy.PostAsync(
                "/api/auth/confirm-email",
                new { userId = Guid.NewGuid(), token = "x" },
                new Dictionary<string, string> { ["X-Forwarded-For"] = $"192.0.2.{i}" });
            await attempt.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
        }

        var same = new Dictionary<string, string> { ["X-Forwarded-For"] = "192.0.2.1" };
        using var second = await proxy.PostAsync("/api/auth/confirm-email", new { userId = Guid.NewGuid(), token = "x" }, same);
        await second.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
        using var limited = await proxy.PostAsync("/api/auth/confirm-email", new { userId = Guid.NewGuid(), token = "x" }, same);
        await AssertRateLimitedAsync(limited);
    }
}
