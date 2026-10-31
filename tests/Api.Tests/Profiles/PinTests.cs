using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Profiles;

/// <summary>The PIN (T-026): format, hashing, the attempt counter and lock, the rate limit.</summary>
public sealed class PinTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private const string Wrong = "9135";

    private AuthHost Host(params (string Key, string Value)[] settings) => KidHost.Create(db.ConnectionString, null, settings);

    private static async Task AssertRetryAfterAsync(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values), "Retry-After header missing");
        Assert.True(int.Parse(values!.Single(), System.Globalization.CultureInfo.InvariantCulture) >= 1);
        await Task.CompletedTask;
    }

    private async Task<(int Failed, DateTimeOffset? LockedUntil, string Hash)> PinRowAsync(Guid userId)
    {
        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT FailedCount, LockedUntil, Hash FROM UserPins WHERE UserId = @u";
        command.Parameters.AddWithValue("@u", userId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetDateTimeOffset(1), reader.GetString(2));
    }

    private static async Task<KidScenario> InKidModeAsync(AuthHost host)
    {
        var s = await host.NewScenarioAsync();
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);
        return s;
    }

    [Fact]
    public async Task Unlock_with_correct_pin()
    {
        await using var host = Host();
        var s = await InKidModeAsync(host);
        using var wrong = await s.Owner.Client.UnlockAsync(Wrong);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(1, (await PinRowAsync(s.Owner.User.Id)).Failed);

        using var response = await s.Owner.Client.UnlockAsync();

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(response.SetCookies(), c => c.StartsWith("__Host-foodplanner=", StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Null, (await s.Owner.Client.MeAsync()).GetProperty("kidMode").ValueKind);
        using var adult = await s.Owner.Client.PostAsync($"/api/households/{s.Household}/tags", new { name = "t" + Guid.NewGuid().ToString("N")[..6], kind = "Tag" });
        Assert.Equal(HttpStatusCode.Created, adult.StatusCode);
        Assert.Equal(0, (await PinRowAsync(s.Owner.User.Id)).Failed);
    }

    [Fact]
    public async Task Unlock_wrong_pin_counts_and_locks()
    {
        await using var host = Host(("Auth:PinMaxFailedAttempts", "5"));
        var s = await InKidModeAsync(host);

        foreach (var remaining in new[] { 4, 3, 2, 1 })
        {
            using var attempt = await s.Owner.Client.UnlockAsync(Wrong);
            await attempt.AssertProblemAsync(HttpStatusCode.Forbidden, "pin-incorrect");
            Assert.Equal(remaining, (await attempt.JsonAsync()).GetProperty("attemptsRemaining").GetInt32());
        }

        using var fifth = await s.Owner.Client.UnlockAsync(Wrong);
        await fifth.AssertProblemAsync(HttpStatusCode.TooManyRequests, "pin-locked");
        await AssertRetryAfterAsync(fifth);

        var hashed = host.Hasher.Verifications;
        using var correctButLocked = await s.Owner.Client.UnlockAsync();
        await correctButLocked.AssertProblemAsync(HttpStatusCode.TooManyRequests, "pin-locked");
        await AssertRetryAfterAsync(correctButLocked);
        Assert.Equal(hashed, host.Hasher.Verifications); // the PIN was not even checked
        Assert.Equal(s.Profile1, (await s.Owner.Client.MeAsync()).GetProperty("kidMode").GetProperty("profileId").GetGuid()); // still in kid mode
        Assert.NotNull((await PinRowAsync(s.Owner.User.Id)).LockedUntil);
    }

    [Fact]
    public async Task Pin_lockout_expires()
    {
        await using var host = Host();
        var s = await InKidModeAsync(host);
        for (var i = 0; i < 5; i++)
        {
            using var attempt = await s.Owner.Client.UnlockAsync(Wrong);
            Assert.True(attempt.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests);
        }

        host.Time.Now += TimeSpan.FromSeconds(119);
        using var early = await s.Owner.Client.UnlockAsync();
        await early.AssertProblemAsync(HttpStatusCode.TooManyRequests, "pin-locked");

        host.Time.Now += TimeSpan.FromSeconds(1); // two minutes in total
        using var unlocked = await s.Owner.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.NoContent, unlocked.StatusCode);
        Assert.Equal(0, (await PinRowAsync(s.Owner.User.Id)).Failed);
    }

    [Fact]
    public async Task A_wrong_pin_after_the_lock_expired_counts_from_zero()
    {
        await using var host = Host();
        var s = await InKidModeAsync(host);
        for (var i = 0; i < 5; i++)
        {
            using var attempt = await s.Owner.Client.UnlockAsync(Wrong);
        }

        host.Time.Now += TimeSpan.FromMinutes(2);
        using var wrong = await s.Owner.Client.UnlockAsync(Wrong);

        await wrong.AssertProblemAsync(HttpStatusCode.Forbidden, "pin-incorrect");
        Assert.Equal(4, (await wrong.JsonAsync()).GetProperty("attemptsRemaining").GetInt32());
    }

    [Fact]
    public async Task Pin_attempts_rate_limited()
    {
        await using var host = Host(("RateLimiting:PinUnlock:PermitLimit", "3"), ("Auth:PinMaxFailedAttempts", "1000"));
        var s = await host.SignInAsync("203.0.113.60");
        var household = await s.Client.CreateHouseholdAsync();
        await s.Client.SetPinAsync(); // from another address: setting the PIN uses the same limit
        var profile = await s.Client.CreateProfileAsync(household);
        await s.Client.EnterKidModeAsync(household, profile);
        s.Client.RemoteIp = "203.0.113.50";

        using var first = await s.Client.UnlockAsync(Wrong);
        using var second = await s.Client.UnlockAsync(Wrong);
        Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        using var third = await s.Client.UnlockAsync(Wrong);
        Assert.Equal(HttpStatusCode.Forbidden, third.StatusCode);
        using var limited = await s.Client.UnlockAsync(Wrong);
        await limited.AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate-limited");
        await AssertRetryAfterAsync(limited);

        // Counted per client IP AND user: another user from the same IP, and the same user from another IP, have their own budget.
        var otherUser = await host.SignInAsync("203.0.113.50");
        var otherHousehold = await otherUser.Client.CreateHouseholdAsync();
        await otherUser.Client.SetPinAsync();
        var otherProfile = await otherUser.Client.CreateProfileAsync(otherHousehold);
        await otherUser.Client.EnterKidModeAsync(otherHousehold, otherProfile);
        s.Client.RemoteIp = "203.0.113.99";
        using var otherIp = await s.Client.UnlockAsync(Wrong);
        Assert.Equal(HttpStatusCode.Forbidden, otherIp.StatusCode);
        using var notLimited = await otherUser.Client.UnlockAsync(Wrong);
        Assert.Equal(HttpStatusCode.Forbidden, notLimited.StatusCode);
    }

    [Fact]
    public async Task Pin_lockout_is_per_account_not_per_cookie()
    {
        await using var host = Host();
        var s = await InKidModeAsync(host);
        using var second = host.CreateClient();
        using var login = await second.LoginAsync(s.Owner.User.Email, AuthHost.Password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        await second.EnterKidModeAsync(s.Household, s.Profile2); // a second device, also in kid mode

        // Three wrong on one device, two on the other: five in all.
        for (var i = 0; i < 3; i++)
        {
            using var attempt = await s.Owner.Client.UnlockAsync(Wrong);
            Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
        }

        using var fourth = await second.UnlockAsync(Wrong);
        Assert.Equal(HttpStatusCode.Forbidden, fourth.StatusCode);
        using var fifth = await second.UnlockAsync(Wrong);
        await fifth.AssertProblemAsync(HttpStatusCode.TooManyRequests, "pin-locked");

        // A fresh password sign-in (a new cookie) does not reset it, and neither device can unlock now.
        using var third = host.CreateClient();
        using var again = await third.LoginAsync(s.Owner.User.Email, AuthHost.Password);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.NotNull((await PinRowAsync(s.Owner.User.Id)).LockedUntil);
        using var stillLocked = await s.Owner.Client.UnlockAsync();
        await stillLocked.AssertProblemAsync(HttpStatusCode.TooManyRequests, "pin-locked");
    }

    [Fact]
    public async Task Parallel_wrong_pins_cannot_beat_the_lockout()
    {
        await using var host = Host(("Auth:PinMaxFailedAttempts", "5"));
        var s = await InKidModeAsync(host);
        var before = host.Hasher.Verifications;

        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(async _ =>
        {
            using var response = await s.Owner.Client.UnlockAsync(Wrong);
            return response.StatusCode;
        }));

        Assert.True(host.Hasher.Verifications - before <= 5, $"{host.Hasher.Verifications - before} PINs were hashed");
        Assert.Contains(HttpStatusCode.TooManyRequests, results);
        Assert.DoesNotContain(HttpStatusCode.NoContent, results);
        Assert.NotNull((await PinRowAsync(s.Owner.User.Id)).LockedUntil);
    }

    [Fact]
    public async Task Unlock_when_not_in_kid_mode_is_409()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var before = host.Hasher.Verifications;

        using var response = await s.Owner.Client.UnlockAsync(Wrong);

        await response.AssertProblemAsync(HttpStatusCode.Conflict, "not-in-kid-mode");
        Assert.Equal(0, (await PinRowAsync(s.Owner.User.Id)).Failed);
        Assert.Equal(before, host.Hasher.Verifications);
    }

    [Fact]
    public async Task Unlock_without_a_pin_is_409_pin_not_set_and_a_malformed_pin_is_400()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var child = await host.JoinAsync(s.Owner, s.Household, "Child");
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);
        using var malformed = await s.Owner.Client.UnlockAsync("12ab");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(0, (await PinRowAsync(s.Owner.User.Id)).Failed); // format errors are not guesses

        // An account without a PIN (here: its PIN row is gone) in kid mode cannot unlock.
        await child.Client.SetPinAsync("2468");
        await child.Client.EnterKidModeAsync(s.Household, s.Profile1);
        await HouseholdScenario.ScalarAsync(db.ConnectionString, "DELETE FROM UserPins WHERE UserId = @u", ("@u", child.User.Id));
        using var none = await child.Client.UnlockAsync("2468");
        await none.AssertProblemAsync(HttpStatusCode.Conflict, "pin-not-set");
    }

    [Theory]
    [InlineData("123")]
    [InlineData("12345")]
    [InlineData("12a4")]
    [InlineData("")]
    [InlineData("0000")]
    [InlineData("1234")]
    [InlineData("9876")]
    public async Task Pin_validation_refuses_bad_pins(string pin)
    {
        await using var host = Host();
        var owner = await host.SignInAsync();

        using var response = await owner.Client.PutAsync("/api/auth/pin", new { pin });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("pin", out _));
        Assert.Equal(0, await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM UserPins WHERE UserId = @u", ("@u", owner.User.Id)) is int n ? n : -1);
    }

    [Fact]
    public async Task Pin_validation_accepts_a_good_pin()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();

        using var response = await owner.Client.PutAsync("/api/auth/pin", new { pin = "4827" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Pin_change_requires_current_pin()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();

        using var none = await s.Owner.Client.PutAsync("/api/auth/pin", new { pin = "5913" });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);

        using var wrong = await s.Owner.Client.PutAsync("/api/auth/pin", new { pin = "5913", currentPin = Wrong });
        await wrong.AssertProblemAsync(HttpStatusCode.Forbidden, "pin-incorrect");
        Assert.Equal(1, (await PinRowAsync(s.Owner.User.Id)).Failed); // counted like a wrong unlock

        using var withPassword = await s.Owner.Client.PutAsync("/api/auth/pin", new Dictionary<string, string> { ["pin"] = "5913", ["password"] = AuthHost.Password });
        Assert.Equal(HttpStatusCode.NoContent, withPassword.StatusCode);
        Assert.Equal(0, (await PinRowAsync(s.Owner.User.Id)).Failed);

        using var withPin = await s.Owner.Client.PutAsync("/api/auth/pin", new { pin = "7246", currentPin = "5913" });
        Assert.Equal(HttpStatusCode.NoContent, withPin.StatusCode);

        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);
        using var oldPin = await s.Owner.Client.UnlockAsync("5913");
        await oldPin.AssertProblemAsync(HttpStatusCode.Forbidden, "pin-incorrect");
        using var newPin = await s.Owner.Client.UnlockAsync("7246");
        Assert.Equal(HttpStatusCode.NoContent, newPin.StatusCode);
    }

    [Fact]
    public async Task A_wrong_proof_to_change_the_pin_can_lock_it()
    {
        await using var host = Host(("Auth:PinMaxFailedAttempts", "2"));
        var s = await host.NewScenarioAsync();

        using var first = await s.Owner.Client.PutAsync("/api/auth/pin", new { pin = "5913", currentPin = Wrong });
        await first.AssertProblemAsync(HttpStatusCode.Forbidden, "pin-incorrect");
        using var second = await s.Owner.Client.PutAsync("/api/auth/pin", new { pin = "5913", currentPin = Wrong });
        await second.AssertProblemAsync(HttpStatusCode.TooManyRequests, "pin-locked");
        using var rightButLocked = await s.Owner.Client.PutAsync("/api/auth/pin", new { pin = "5913", currentPin = KidHost.Pin });
        await rightButLocked.AssertProblemAsync(HttpStatusCode.TooManyRequests, "pin-locked");
    }

    [Fact]
    public async Task Pin_stored_hashed()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = KidHost.Create(db.ConnectionString, services => services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs));
        var one = await host.SignInAsync();
        var two = await host.SignInAsync();
        const string pin = "7391";
        await one.Client.SetPinAsync(pin);
        await two.Client.SetPinAsync(pin);
        var household = await one.Client.CreateHouseholdAsync();
        var profile = await one.Client.CreateProfileAsync(household);
        await one.Client.EnterKidModeAsync(household, profile);
        using var wrong = await one.Client.UnlockAsync("7392");
        using var right = await one.Client.UnlockAsync(pin);

        var hashOne = (await PinRowAsync(one.User.Id)).Hash;
        var hashTwo = (await PinRowAsync(two.User.Id)).Hash;

        Assert.DoesNotContain(pin, hashOne, StringComparison.Ordinal);
        Assert.NotEqual(hashOne, hashTwo); // salted
        lock (logs.Messages)
        {
            Assert.NotEmpty(logs.Messages);
            Assert.DoesNotContain(logs.Messages, m => m.Contains(pin, StringComparison.Ordinal) || m.Contains("7392", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Pin_endpoints_blocked_in_kid_mode()
    {
        await using var host = Host();
        var s = await InKidModeAsync(host);

        using var response = await s.Owner.Client.PutAsync("/api/auth/pin", new { pin = "5913", currentPin = KidHost.Pin });

        await response.AssertProblemAsync(HttpStatusCode.Forbidden, "kid-mode");
        Assert.Equal(0, (await PinRowAsync(s.Owner.User.Id)).Failed); // refused before any check
    }

    [Fact]
    public async Task Kid_mode_cannot_create_or_accept_or_leave()
    {
        await using var host = Host();
        var s = await InKidModeAsync(host);
        var other = await host.SignInAsync();
        var otherHousehold = await other.Client.CreateHouseholdAsync();
        var invite = await other.Client.CreateInviteAsync(otherHousehold, "Child");

        using var create = await s.Owner.Client.PostAsync("/api/households", new { name = "New" });
        await create.AssertProblemAsync(HttpStatusCode.Forbidden, "kid-mode");
        using var accept = await s.Owner.Client.AcceptAsync(invite);
        await accept.AssertProblemAsync(HttpStatusCode.Forbidden, "kid-mode");
        using var leave = await s.Owner.Client.PostAsync($"/api/households/{s.Household}/leave");
        await leave.AssertProblemAsync(HttpStatusCode.Forbidden, "kid-mode");
        using var logout = await s.Owner.Client.PostAsync("/api/auth/logout");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode); // signing out stays open
    }
}
