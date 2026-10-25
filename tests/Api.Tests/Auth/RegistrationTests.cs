using System.Net;
using System.Text.RegularExpressions;
using Foodplanner.Api.Features.Auth;
using Foodplanner.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Foodplanner.Api.Tests.Auth;

/// <summary>T-072 (Q-011): register records only the address; the emailed link is where the password is chosen.</summary>
public sealed partial class RegistrationTests(AuthDatabase db) : IClassFixture<AuthDatabase>, IAsyncLifetime
{
    private const string VictimPassword = "the victim's own password";
    private const string AttackerPassword = "attacker chosen password";

    private AuthHost _host = null!;

    public ValueTask InitializeAsync()
    {
        // No resend cooldown by default, so tests can register twice in a row; the cooldown has its own test.
        _host = AuthHost.Create(db.ConnectionString, new Dictionary<string, string?> { ["Auth:ResendCooldownMinutes"] = "0" });
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [GeneratedRegex(@"/set-password\?userId=(?<id>[0-9a-fA-F-]{36})&token=(?<token>\S+)")]
    private static partial Regex LinkPattern();

    private static (string UserId, string Token) ParseLink(string body)
    {
        var match = LinkPattern().Match(body);
        Assert.True(match.Success, "no set-password link in mail");
        return (match.Groups["id"].Value, Uri.UnescapeDataString(match.Groups["token"].Value));
    }

    /// <summary>The link in the newest mail to the address, i.e. what the owner of that inbox sees.</summary>
    private (string UserId, string Token) LatestLinkFor(string email) =>
        ParseLink(_host.Email.Messages.Last(m => m.To == email && m.TextBody.Contains("set-password", StringComparison.Ordinal)).TextBody);

    private async Task<HttpResponseMessage> RegisterAsync(ApiClient client, string email) =>
        await client.PostAsync("/api/auth/register", new { email });

    private static Task<HttpResponseMessage> SetPasswordAsync(ApiClient client, (string UserId, string Token) link, string password) =>
        client.PostAsync("/api/auth/confirm-email", new { userId = link.UserId, token = link.Token, password });

    private async Task<bool> PasswordIs(string email, string password)
    {
        var user = await _host.FindUserAsync(email);
        if (user.PasswordHash is null)
        {
            return false;
        }

        using var scope = _host.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
        return hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Success;
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    public async Task Register_validation(string email)
    {
        using var client = _host.CreateClient();

        using var response = await RegisterAsync(client, email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.JsonAsync();
        Assert.True(body.GetProperty("errors").TryGetProperty("email", out var messages));
        Assert.NotEmpty(messages.EnumerateArray());
        Assert.DoesNotContain(_host.Email.Messages, m => m.To == email);
    }

    [Fact]
    public async Task Register_stores_no_password_and_sends_a_set_password_link()
    {
        var email = AuthHost.NewEmail();
        using var client = _host.CreateClient();

        // A password in the body is ignored: the contract has no such field any more.
        using var response = await client.PostAsync("/api/auth/register", new { email, password = AttackerPassword });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var user = await _host.FindUserAsync(email);
        Assert.Null(user.PasswordHash);
        Assert.False(user.EmailConfirmed);
        var mail = Assert.Single(_host.Email.Messages, m => m.To == email);
        Assert.Contains("/set-password?userId=", mail.TextBody, StringComparison.Ordinal);
        using var login = await client.LoginAsync(email, AttackerPassword);
        await login.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");
    }

    [Fact]
    public async Task Register_is_uniform_for_new_confirmed_and_unconfirmed_addresses()
    {
        var confirmed = AuthHost.NewEmail();
        await _host.CreateUserAsync(confirmed);
        var unconfirmed = AuthHost.NewEmail();
        using var client = _host.CreateClient();
        using var first = await RegisterAsync(client, unconfirmed);

        using var forNew = await RegisterAsync(client, AuthHost.NewEmail());
        using var forConfirmed = await RegisterAsync(client, confirmed);
        using var forUnconfirmed = await RegisterAsync(client, unconfirmed);

        var bodies = new List<string>();
        foreach (var response in new[] { forNew, forConfirmed, forUnconfirmed })
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.DoesNotContain(response.SetCookies(), c => c.StartsWith("__Host-foodplanner=", StringComparison.Ordinal));
            bodies.Add(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        Assert.Single(bodies.Distinct());
        using var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode); // register never signs in
    }

    [Fact]
    public async Task Register_for_a_confirmed_address_sends_a_notice_without_a_link()
    {
        var existing = AuthHost.NewEmail();
        await _host.CreateUserAsync(existing);
        using var client = _host.CreateClient();

        using var response = await RegisterAsync(client, existing);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var notice = Assert.Single(_host.Email.Messages, m => m.To == existing);
        Assert.Contains("already have", notice.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", notice.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userId", notice.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_for_an_unconfirmed_address_sends_a_fresh_link_and_kills_the_earlier_one()
    {
        var email = AuthHost.NewEmail();
        using var client = _host.CreateClient();
        using var first = await RegisterAsync(client, email);
        var oldLink = LatestLinkFor(email);

        using var second = await RegisterAsync(client, email);
        var newLink = LatestLinkFor(email);

        Assert.Equal(2, _host.Email.Messages.Count(m => m.To == email));
        Assert.NotEqual(oldLink.Token, newLink.Token);
        using var withOld = await SetPasswordAsync(client, oldLink, VictimPassword);
        await withOld.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
        Assert.False((await _host.FindUserAsync(email)).EmailConfirmed);
        using var withNew = await SetPasswordAsync(client, newLink, VictimPassword);
        Assert.Equal(HttpStatusCode.NoContent, withNew.StatusCode);
    }

    [Fact]
    public async Task Set_password_flow_sets_the_password_confirms_and_does_not_sign_in()
    {
        var email = AuthHost.NewEmail();
        using var client = _host.CreateClient();
        using var register = await RegisterAsync(client, email);
        var link = LatestLinkFor(email);

        using var beforeConfirm = await client.LoginAsync(email, VictimPassword);
        await beforeConfirm.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");

        using var invalid = await SetPasswordAsync(client, (link.UserId, link.Token + "x"), VictimPassword);
        await invalid.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
        using var wrongUser = await SetPasswordAsync(client, (Guid.NewGuid().ToString(), link.Token), VictimPassword);
        await wrongUser.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");

        using var confirmed = await SetPasswordAsync(client, link, VictimPassword);
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        Assert.DoesNotContain(confirmed.SetCookies(), c => c.StartsWith("__Host-foodplanner=", StringComparison.Ordinal));
        using var meAfter = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meAfter.StatusCode);
        using var login = await client.LoginAsync(email, VictimPassword);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        using var reused = await SetPasswordAsync(client, link, "another password entirely");
        await reused.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
        Assert.True(await PasswordIs(email, VictimPassword));
    }

    [Theory]
    [InlineData("too short")]
    [InlineData("123456789")]
    [InlineData("")]
    public async Task Set_password_validates_the_password_and_keeps_the_link_usable(string password)
    {
        var email = AuthHost.NewEmail();
        using var client = _host.CreateClient();
        using var register = await RegisterAsync(client, email);
        var link = LatestLinkFor(email);

        using var weak = await SetPasswordAsync(client, link, password);

        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        var body = await weak.JsonAsync();
        Assert.True(body.GetProperty("errors").TryGetProperty("password", out var messages));
        Assert.NotEmpty(messages.EnumerateArray());
        var user = await _host.FindUserAsync(email);
        Assert.Null(user.PasswordHash);
        Assert.False(user.EmailConfirmed);
        using var good = await SetPasswordAsync(client, link, VictimPassword);
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
    }

    [Fact]
    public async Task Set_password_expired_link()
    {
        var email = AuthHost.NewEmail();
        using var client = _host.CreateClient();
        using var register = await RegisterAsync(client, email);
        var link = LatestLinkFor(email);

        _host.Time.Now = _host.Time.Now.AddHours(24).AddMinutes(1);
        using var expired = await SetPasswordAsync(client, link, VictimPassword);

        await expired.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
        Assert.False((await _host.FindUserAsync(email)).EmailConfirmed);
    }

    [Fact]
    public async Task Set_password_just_before_expiry_works()
    {
        var email = AuthHost.NewEmail();
        using var client = _host.CreateClient();
        using var register = await RegisterAsync(client, email);
        var link = LatestLinkFor(email);

        _host.Time.Now = _host.Time.Now.AddHours(23).AddMinutes(59);
        using var confirmed = await SetPasswordAsync(client, link, VictimPassword);

        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
    }

    [Fact]
    public async Task Set_password_with_garbage_user_id_is_a_confirmation_problem()
    {
        using var client = _host.CreateClient();

        using var response = await client.PostAsync("/api/auth/confirm-email", new { userId = "nope", token = "nope", password = VictimPassword });

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
    }

    [Fact]
    public async Task Two_submissions_of_one_link_cannot_both_win()
    {
        var email = AuthHost.NewEmail();
        using var client = _host.CreateClient();
        using var other = _host.CreateClient();
        using var register = await RegisterAsync(client, email);
        var link = LatestLinkFor(email);

        var results = await Task.WhenAll(SetPasswordAsync(client, link, VictimPassword), SetPasswordAsync(other, link, AttackerPassword));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_of_an_account_without_a_password_is_refused_and_still_hashes()
    {
        var email = AuthHost.NewEmail();
        await _host.CreateUserAsync(email, confirmed: false, password: null);
        using var client = _host.CreateClient();
        using var unknown = await client.LoginAsync(AuthHost.NewEmail(), AuthHost.Password);
        var before = _host.Hasher.Verifications;

        using var passwordless = await client.LoginAsync(email, AuthHost.Password);

        await passwordless.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");
        var unknownBody = await unknown.JsonAsync();
        var passwordlessBody = await passwordless.JsonAsync();
        foreach (var field in new[] { "type", "title", "detail" })
        {
            Assert.Equal(unknownBody.GetProperty(field).GetString(), passwordlessBody.GetProperty(field).GetString());
        }

        Assert.Equal(before + 1, _host.Hasher.Verifications);
    }

    // Q-011 race, both orders. Whoever completes set-password through the inbox link chooses the password; the
    // registrant who cannot read the inbox never learns a link and so never sets or knows the password.

    [Fact]
    public async Task Race_attacker_registers_first_then_the_victim()
    {
        var email = AuthHost.NewEmail();
        using var attacker = _host.CreateClient();
        using var victim = _host.CreateClient();
        using var byAttacker = await RegisterAsync(attacker, email);
        using var byVictim = await RegisterAsync(victim, email);
        Assert.Equal(
            await byAttacker.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await byVictim.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)); // the attacker learns nothing

        // Only the inbox owner has links: the first one is dead, the latest works.
        var links = _host.Email.Messages.Where(m => m.To == email).Select(m => ParseLink(m.TextBody)).ToList();
        using var staleLink = await SetPasswordAsync(victim, links[0], VictimPassword);
        await staleLink.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
        using var viaInbox = await SetPasswordAsync(victim, links[1], VictimPassword);
        Assert.Equal(HttpStatusCode.NoContent, viaInbox.StatusCode);

        Assert.True(await PasswordIs(email, VictimPassword));
        Assert.False(await PasswordIs(email, AttackerPassword));
        using var attackerLogin = await attacker.LoginAsync(email, AttackerPassword);
        await attackerLogin.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");
        using var victimLogin = await victim.LoginAsync(email, VictimPassword);
        Assert.Equal(HttpStatusCode.NoContent, victimLogin.StatusCode);
    }

    [Fact]
    public async Task Race_the_victim_registers_first_then_the_attacker()
    {
        var email = AuthHost.NewEmail();
        using var attacker = _host.CreateClient();
        using var victim = _host.CreateClient();
        using var byVictim = await RegisterAsync(victim, email);
        using var byAttacker = await RegisterAsync(attacker, email);

        // The victim's first link was rotated away by the attacker's registration; the newest lands in the victim's
        // inbox and sets whatever password the victim types. The attacker never sees a link.
        var links = _host.Email.Messages.Where(m => m.To == email).Select(m => ParseLink(m.TextBody)).ToList();
        using var staleLink = await SetPasswordAsync(victim, links[0], VictimPassword);
        await staleLink.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
        using var viaInbox = await SetPasswordAsync(victim, links[1], VictimPassword);
        Assert.Equal(HttpStatusCode.NoContent, viaInbox.StatusCode);

        Assert.True(await PasswordIs(email, VictimPassword));
        Assert.False(await PasswordIs(email, AttackerPassword));
        // The attacker cannot confirm or change anything afterwards either: the link is single use.
        using var replay = await SetPasswordAsync(attacker, links[1], AttackerPassword);
        await replay.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
        Assert.True(await PasswordIs(email, VictimPassword));
    }

    [Fact]
    public async Task Cleanup_removes_unconfirmed_accounts_after_the_window_and_the_address_can_register_again()
    {
        var squatted = AuthHost.NewEmail();
        var fresh = AuthHost.NewEmail();
        var confirmed = AuthHost.NewEmail();
        await _host.CreateUserAsync(confirmed);
        using var client = _host.CreateClient();
        using var r1 = await RegisterAsync(client, squatted);
        _host.Time.Now = _host.Time.Now.AddHours(20);
        using var r2 = await RegisterAsync(client, fresh);
        var cleanup = _host.Services.GetServices<IHostedService>().OfType<UnconfirmedAccountCleanup>().Single();

        _host.Time.Now = _host.Time.Now.AddHours(3); // squatted is 23 h old, fresh is 3 h old
        Assert.Equal(0, await cleanup.SweepAsync(TestContext.Current.CancellationToken));
        _host.Time.Now = _host.Time.Now.AddHours(2).AddMinutes(1); // squatted is over 24 h old
        var deleted = await cleanup.SweepAsync(TestContext.Current.CancellationToken);

        Assert.True(deleted >= 1); // other tests of this class may have left expired accounts too
        Assert.Null(await FindOrNullAsync(squatted));
        Assert.NotNull(await FindOrNullAsync(fresh));
        Assert.NotNull(await FindOrNullAsync(confirmed)); // confirmed accounts never expire
        using var again = await RegisterAsync(client, squatted);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        using var set = await SetPasswordAsync(client, LatestLinkFor(squatted), VictimPassword);
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
    }

    [Fact]
    public async Task Cleanup_does_not_touch_an_account_that_set_its_password()
    {
        var email = AuthHost.NewEmail();
        using var client = _host.CreateClient();
        using var register = await RegisterAsync(client, email);
        using var set = await SetPasswordAsync(client, LatestLinkFor(email), VictimPassword);
        var cleanup = _host.Services.GetServices<IHostedService>().OfType<UnconfirmedAccountCleanup>().Single();

        _host.Time.Now = _host.Time.Now.AddDays(3);
        await cleanup.SweepAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(await FindOrNullAsync(email));
    }

    [Fact]
    public async Task Cleanup_works_in_batches()
    {
        await using var host = AuthHost.Create(db.ConnectionString, new Dictionary<string, string?> { ["Auth:UnconfirmedCleanupBatchSize"] = "2" });
        var emails = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            emails.Add(AuthHost.NewEmail());
            await host.CreateUserAsync(emails[i], confirmed: false, password: null);
        }

        var cleanup = host.Services.GetServices<IHostedService>().OfType<UnconfirmedAccountCleanup>().Single();
        // First pass stamps accounts that have no registration time yet (they pre-date the claim); then they age out.
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(0, await cleanup.SweepAsync(TestContext.Current.CancellationToken));
        }

        host.Time.Now = host.Time.Now.AddHours(25);

        Assert.True(await cleanup.SweepAsync(TestContext.Current.CancellationToken) >= 5); // more than one batch of 2
        foreach (var email in emails)
        {
            Assert.Null(await FindUserOrNullAsync(host, email));
        }
    }

    [Fact]
    public async Task Repeat_register_within_the_cooldown_does_nothing_and_after_it_sends_a_fresh_link()
    {
        await using var host = AuthHost.Create(db.ConnectionString); // default cooldown: 5 minutes
        var email = AuthHost.NewEmail();
        using var client = host.CreateClient();
        using var first = await RegisterAsync(client, email);
        var firstLink = ParseLink(Assert.Single(host.Email.Messages, m => m.To == email).TextBody);

        host.Time.Now = host.Time.Now.AddMinutes(4);
        using var tooSoon = await RegisterAsync(client, email);

        Assert.Equal(HttpStatusCode.Accepted, tooSoon.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await tooSoon.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Single(host.Email.Messages, m => m.To == email); // no new mail, the first link was not killed
        host.Time.Now = host.Time.Now.AddMinutes(2);
        using var later = await RegisterAsync(client, email);
        Assert.Equal(2, host.Email.Messages.Count(m => m.To == email));
        using var oldLink = await SetPasswordAsync(client, firstLink, VictimPassword);
        await oldLink.AssertProblemAsync(HttpStatusCode.BadRequest, "confirmation-invalid");
    }

    [Fact]
    public async Task Cleanup_goes_by_the_newest_registration_claim_when_there_are_duplicates()
    {
        var email = AuthHost.NewEmail();
        using var client = _host.CreateClient();
        using var register = await RegisterAsync(client, email);
        var user = await _host.FindUserAsync(email);
        using (var scope = _host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            // An old duplicate, as an earlier version or a race could have left behind.
            await users.AddClaimAsync(user, new System.Security.Claims.Claim("foodplanner:registered-at", _host.Time.Now.AddDays(-10).ToUnixTimeSeconds().ToString("D12")));
        }

        var cleanup = _host.Services.GetServices<IHostedService>().OfType<UnconfirmedAccountCleanup>().Single();
        _host.Time.Now = _host.Time.Now.AddHours(1);
        await cleanup.SweepAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(await FindOrNullAsync(email)); // the newest claim is one hour old
        using var again = await RegisterAsync(client, email);
        using var scope2 = _host.Services.CreateScope();
        var users2 = scope2.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var claims = await users2.GetClaimsAsync((await users2.FindByEmailAsync(email))!);
        Assert.Single(claims, c => c.Type == "foodplanner:registered-at"); // a refresh leaves exactly one
    }

    [Fact]
    public async Task Cleanup_is_not_blocked_by_a_membership_row_of_an_expired_account()
    {
        // Unconfirmed accounts normally have none (they cannot sign in), but the cascade must not block the delete
        // if one ever exists, for example from a manual fix or a future flow.
        var stale = AuthHost.NewEmail();
        using var client = _host.CreateClient();
        using var register = await RegisterAsync(client, stale);
        var user = await _host.FindUserAsync(stale);
        using (var scope = _host.Services.CreateScope())
        {
            var resolution = scope.ServiceProvider.GetRequiredService<Foodplanner.Infrastructure.Persistence.Tenancy.ITenantResolution>();
            await resolution.CreateHouseholdWithOwner("Squatter home", user.Id, TestContext.Current.CancellationToken);
        }

        var cleanup = _host.Services.GetServices<IHostedService>().OfType<UnconfirmedAccountCleanup>().Single();
        _host.Time.Now = _host.Time.Now.AddHours(25);
        var deleted = await cleanup.SweepAsync(TestContext.Current.CancellationToken);

        Assert.True(deleted >= 1);
        Assert.Null(await FindOrNullAsync(stale));
    }

    private async Task<AppUser?> FindOrNullAsync(string email) => await FindUserOrNullAsync(_host, email);

    private static async Task<AppUser?> FindUserOrNullAsync(AuthHost host, string email)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email);
    }
}
