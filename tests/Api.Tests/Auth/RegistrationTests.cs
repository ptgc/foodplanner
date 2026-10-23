using System.Net;
using System.Text.RegularExpressions;

namespace Foodplanner.Api.Tests.Auth;

public sealed partial class RegistrationTests(AuthDatabase db) : IClassFixture<AuthDatabase>, IAsyncLifetime
{
    private AuthHost _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = AuthHost.Create(db.ConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [GeneratedRegex(@"userId=(?<id>[0-9a-fA-F-]{36})&token=(?<token>\S+)")]
    private static partial Regex LinkPattern();

    private static (string UserId, string Token) ParseLink(string body)
    {
        var match = LinkPattern().Match(body);
        Assert.True(match.Success, "no confirmation link in mail");
        return (match.Groups["id"].Value, Uri.UnescapeDataString(match.Groups["token"].Value));
    }

    private (string UserId, string Token) ConfirmationFor(string email)
    {
        var mail = Assert.Single(_host.Email.Messages, m => m.To == email);
        return ParseLink(mail.TextBody);
    }

    [Theory]
    [InlineData("not-an-email", "a long enough password", "email")]
    [InlineData("", "a long enough password", "email")]
    [InlineData("someone@example.com", "too short", "password")]
    [InlineData("someone@example.com", "123456789", "password")]
    public async Task Register_validation(string email, string password, string field)
    {
        using var client = _host.CreateClient();

        using var response = await client.PostAsync("/api/auth/register", new { email, password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.JsonAsync();
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out var messages));
        Assert.NotEmpty(messages.EnumerateArray());
        Assert.DoesNotContain(_host.Email.Messages, m => m.To == email);
    }

    [Fact]
    public async Task Register_is_uniform()
    {
        var existing = AuthHost.NewEmail();
        await _host.CreateUserAsync(existing);
        var fresh = AuthHost.NewEmail();
        using var client = _host.CreateClient();

        using var forNew = await client.PostAsync("/api/auth/register", new { email = fresh, password = AuthHost.Password });
        using var forExisting = await client.PostAsync("/api/auth/register", new { email = existing, password = AuthHost.Password });

        Assert.Equal(HttpStatusCode.Accepted, forNew.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, forExisting.StatusCode);
        Assert.Equal(
            await forNew.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await forExisting.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(forNew.SetCookies().Concat(forExisting.SetCookies()), c => c.StartsWith("__Host-foodplanner=", StringComparison.Ordinal));
        using var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode); // register never signs in
    }

    [Fact]
    public async Task Register_sends_confirmation_or_notice()
    {
        var existing = AuthHost.NewEmail();
        await _host.CreateUserAsync(existing);
        var fresh = AuthHost.NewEmail();
        using var client = _host.CreateClient();

        using var forNew = await client.PostAsync("/api/auth/register", new { email = fresh, password = AuthHost.Password });
        using var forExisting = await client.PostAsync("/api/auth/register", new { email = existing, password = AuthHost.Password });

        var confirmation = Assert.Single(_host.Email.Messages, m => m.To == fresh);
        Assert.Contains("token=", confirmation.TextBody, StringComparison.Ordinal);
        var notice = Assert.Single(_host.Email.Messages, m => m.To == existing);
        Assert.Contains("already have", notice.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", notice.TextBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_for_unconfirmed_existing_address_sends_a_fresh_confirmation()
    {
        var email = AuthHost.NewEmail();
        using var client = _host.CreateClient();
        using var first = await client.PostAsync("/api/auth/register", new { email, password = AuthHost.Password });
        _host.Time.Now = _host.Time.Now.AddHours(25); // the first link has expired

        using var second = await client.PostAsync("/api/auth/register", new { email, password = "another long password" });

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var (userId, token) = ConfirmationForLast(email);
        using var confirm = await client.PostAsync("/api/auth/confirm-email", new { userId, token });
        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);
        // The first password stays: the second registration did not change it.
        using var login = await client.LoginAsync(email, AuthHost.Password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    private (string UserId, string Token) ConfirmationForLast(string email) =>
        ParseLink(_host.Email.Messages.Last(m => m.To == email).TextBody);

    [Fact]
    public async Task Confirm_email_flow()
    {
        var email = AuthHost.NewEmail();
        using var client = _host.CreateClient();
        using var register = await client.PostAsync("/api/auth/register", new { email, password = AuthHost.Password });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        var (userId, token) = ConfirmationFor(email);

        using var beforeConfirm = await client.LoginAsync(email, AuthHost.Password);
        await beforeConfirm.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");

        using var invalid = await client.PostAsync("/api/auth/confirm-email", new { userId, token = token + "x" });
        await invalid.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
        using var wrongUser = await client.PostAsync("/api/auth/confirm-email", new { userId = Guid.NewGuid(), token });
        await wrongUser.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");

        using var confirmed = await client.PostAsync("/api/auth/confirm-email", new { userId, token });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        using var login = await client.LoginAsync(email, AuthHost.Password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        using var reused = await client.PostAsync("/api/auth/confirm-email", new { userId, token });
        await reused.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
    }

    [Fact]
    public async Task Confirm_email_expired_token()
    {
        var email = AuthHost.NewEmail();
        using var client = _host.CreateClient();
        using var register = await client.PostAsync("/api/auth/register", new { email, password = AuthHost.Password });
        var (userId, token) = ConfirmationFor(email);

        _host.Time.Now = _host.Time.Now.AddHours(24).AddMinutes(1);
        using var expired = await client.PostAsync("/api/auth/confirm-email", new { userId, token });

        await expired.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
        Assert.False((await _host.FindUserAsync(email)).EmailConfirmed);
    }

    [Fact]
    public async Task Confirm_email_just_before_expiry_works()
    {
        var email = AuthHost.NewEmail();
        using var client = _host.CreateClient();
        using var register = await client.PostAsync("/api/auth/register", new { email, password = AuthHost.Password });
        var (userId, token) = ConfirmationFor(email);

        _host.Time.Now = _host.Time.Now.AddHours(23).AddMinutes(59);
        using var confirmed = await client.PostAsync("/api/auth/confirm-email", new { userId, token });

        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
    }

    [Fact]
    public async Task Confirm_email_with_garbage_user_id_is_a_confirmation_problem()
    {
        using var client = _host.CreateClient();

        using var response = await client.PostAsync("/api/auth/confirm-email", new { userId = "nope", token = "nope" });

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
    }
}
