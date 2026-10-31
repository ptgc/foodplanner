using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Foodplanner.Api.Features.MealPlans;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.ShoppingLists;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foodplanner.Api.Tests.Profiles;

/// <summary>Records every announced plan change; can be told to fail, to prove the change survives a failing notifier.</summary>
internal sealed class RecordingMealPlanNotifier : IMealPlanNotifier
{
    public List<MealPlanChange> Changes { get; } = [];

    public bool Fail { get; set; }

    public Task NotifyAsync(MealPlanChange change, CancellationToken cancellationToken)
    {
        lock (Changes)
        {
            Changes.Add(change);
        }

        return Fail ? throw new InvalidOperationException("the hub is down") : Task.CompletedTask;
    }
}

/// <summary>Kids make the week plan (T-026): attribution, dinners only, the week lock and the live signal.</summary>
public sealed class KidMealPlanTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly string Week = Monday.ToString("yyyy-MM-dd");

    private AuthHost Host(Action<IServiceCollection>? configure = null) => KidHost.Create(db.ConnectionString, configure);

    private static string Url(Guid household, string rest = "", string? week = null) =>
        $"/api/households/{household}/meal-plans/{week ?? Week}{rest}";

    /// <summary>A household with an Owner (kid device: <c>Owner.Client</c> after Enter), a second full session of the same account, an Adult and a Child member.</summary>
    private sealed record Setup(KidScenario S, ApiClient Admin, SignedInUser Adult, SignedInUser Child, Guid Dinner, Guid Dessert);

    private static async Task<Setup> NewAsync(AuthHost host)
    {
        var s = await host.NewScenarioAsync();
        var admin = host.CreateClient();
        using var login = await admin.LoginAsync(s.Owner.User.Email, AuthHost.Password);
        var adult = await host.JoinAsync(s.Owner, s.Household, "Adult");
        var child = await host.JoinAsync(s.Owner, s.Household, "Child");
        var dinner = await admin.CreateRecipeAsync(s.Household, KidHost.Middag);
        var dessert = await admin.CreateRecipeAsync(s.Household, KidHost.Dessert);
        return new Setup(s, admin, adult, child, dinner, dessert);
    }

    private static async Task<HttpResponseMessage> AddAsync(ApiClient client, Guid household, Guid recipe, DateOnly? date = null, string? week = null, object? extra = null) =>
        await client.PostAsync(Url(household, "/entries", week), extra ?? new { date = (date ?? Monday).ToString("yyyy-MM-dd"), recipeId = recipe });

    private static async Task<Guid> AddOkAsync(ApiClient client, Guid household, Guid recipe, DateOnly? date = null, string? week = null)
    {
        using var response = await AddAsync(client, household, recipe, date, week);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private async Task<int> EntryCountAsync(Guid household) =>
        (int)(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM MealPlanEntries WHERE HouseholdId = @h", ("@h", household)))!;

    private static async Task AssertWeekLockedAsync(HttpResponseMessage response) =>
        await response.AssertProblemAsync(HttpStatusCode.Conflict, "week-locked");

    [Fact]
    public async Task Kid_pick_is_attributed_to_profile()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        await x.S.Owner.Client.EnterKidModeAsync(x.S.Household, x.S.Profile1);

        using var response = await AddAsync(
            x.S.Owner.Client, x.S.Household, x.Dinner,
            extra: new { date = Week, recipeId = x.Dinner, pickedByProfileId = x.S.Profile2, pickedByUserId = x.Adult.User.Id });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.JsonAsync();
        Assert.Equal(x.S.Profile1, created.GetProperty("pickedByProfile").GetProperty("id").GetGuid());
        Assert.Equal("Mia", created.GetProperty("pickedByProfile").GetProperty("displayName").GetString());
        Assert.Equal(x.S.Owner.User.Id, created.GetProperty("pickedBy").GetProperty("userId").GetGuid());

        using var plan = await x.Admin.GetAsync(Url(x.S.Household));
        var entry = (await plan.JsonAsync()).GetProperty("entries")[0];
        Assert.Equal(x.S.Profile1, entry.GetProperty("pickedByProfile").GetProperty("id").GetGuid());

        // An adult's own pick has no profile.
        var byAdult = await AddOkAsync(x.Admin, x.S.Household, x.Dessert);
        using var again = await x.Admin.GetAsync(Url(x.S.Household));
        var adultEntry = (await again.JsonAsync()).GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == byAdult);
        Assert.Equal(JsonValueKind.Null, adultEntry.GetProperty("pickedByProfile").ValueKind);
    }

    [Fact]
    public async Task Kid_mode_picks_dinners_only()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        await x.S.Owner.Client.EnterKidModeAsync(x.S.Household, x.S.Profile1);

        using var dinner = await AddAsync(x.S.Owner.Client, x.S.Household, x.Dinner);
        Assert.Equal(HttpStatusCode.Created, dinner.StatusCode);
        using var dessert = await AddAsync(x.S.Owner.Client, x.S.Household, x.Dessert);
        await dessert.AssertProblemAsync(HttpStatusCode.Conflict, "pick-not-allowed");
        var untagged = await x.Admin.CreateRecipeAsync(x.S.Household);
        using var noTags = await AddAsync(x.S.Owner.Client, x.S.Household, untagged);
        await noTags.AssertProblemAsync(HttpStatusCode.Conflict, "pick-not-allowed");
        Assert.Equal(1, await EntryCountAsync(x.S.Household));

        // An adult outside kid mode, and a real Child member, may add any recipe.
        await AddOkAsync(x.Admin, x.S.Household, x.Dessert);
        await AddOkAsync(x.Child.Client, x.S.Household, x.Dessert);
        Assert.Equal(3, await EntryCountAsync(x.S.Household));
    }

    [Fact]
    public async Task Adult_locks_and_unlocks_week()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        var stranger = await host.SignInAsync();

        foreach (var user in new[] { x.S.Owner.Client, x.Adult.Client })
        {
            using var locked = await user.PutAsync(Url(x.S.Household, "/lock"));
            Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
            var body = await locked.JsonAsync();
            Assert.True(body.GetProperty("locked").GetBoolean());
            Assert.Equal(host.Time.Now, body.GetProperty("lockedAt").GetDateTimeOffset());
            using var unlocked = await user.DeleteAsync(Url(x.S.Household, "/lock"));
            Assert.Equal(HttpStatusCode.NoContent, unlocked.StatusCode);
            using var read = await user.GetAsync(Url(x.S.Household));
            Assert.False((await read.JsonAsync()).GetProperty("locked").GetBoolean());
        }

        // Locking twice keeps the first locker and time; unlocking an unlocked week is fine.
        using var first = await x.Adult.Client.PutAsync(Url(x.S.Household, "/lock"));
        var at = (await first.JsonAsync()).GetProperty("lockedAt").GetDateTimeOffset();
        host.Time.Now += TimeSpan.FromHours(1);
        using var second = await x.S.Owner.Client.PutAsync(Url(x.S.Household, "/lock"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.JsonAsync();
        Assert.Equal(at, secondBody.GetProperty("lockedAt").GetDateTimeOffset());
        Assert.Equal(x.Adult.User.Id, secondBody.GetProperty("lockedBy").GetProperty("userId").GetGuid());
        Assert.Equal(x.Adult.User.Email, secondBody.GetProperty("lockedBy").GetProperty("email").GetString());
        using var unlock = await x.Adult.Client.DeleteAsync(Url(x.S.Household, "/lock"));
        using var unlockAgain = await x.Adult.Client.DeleteAsync(Url(x.S.Household, "/lock"));
        Assert.Equal(HttpStatusCode.NoContent, unlockAgain.StatusCode);

        // Child member, kid-mode session and non-member: 403. A bad week is 400 on weekStart.
        await x.S.Owner.Client.EnterKidModeAsync(x.S.Household, x.S.Profile1);
        foreach (var client in new[] { x.Child.Client, x.S.Owner.Client, stranger.Client })
        {
            using var put = await client.PutAsync(Url(x.S.Household, "/lock"));
            Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
            using var delete = await client.DeleteAsync(Url(x.S.Household, "/lock"));
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        }

        using var notMonday = await x.Admin.PutAsync(Url(x.S.Household, "/lock", "2026-10-27"));
        Assert.Equal(HttpStatusCode.BadRequest, notMonday.StatusCode);
        Assert.True((await notMonday.JsonAsync()).GetProperty("errors").TryGetProperty("weekStart", out _));
    }

    [Fact]
    public async Task Locking_a_week_without_a_plan_creates_one_empty_plan()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        var empty = "2026-11-09";

        using var locked = await x.Admin.PutAsync(Url(x.S.Household, "/lock", empty));
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);

        Assert.Equal(1, (int)(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM MealPlans WHERE HouseholdId = @h AND WeekStart = '2026-11-09'", ("@h", x.S.Household)))!);
        using var read = await x.Admin.GetAsync(Url(x.S.Household, "", empty));
        var body = await read.JsonAsync();
        Assert.True(body.GetProperty("locked").GetBoolean());
        Assert.Equal(0, body.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Kid_write_to_locked_week_refused()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        var own = await ProfilePickAsync(host, x, x.S.Profile1);
        var othersPick = await AddOkAsync(x.Adult.Client, x.S.Household, x.Dinner, Monday.AddDays(1));
        using var lockWeek = await x.Admin.PutAsync(Url(x.S.Household, "/lock"));
        Assert.Equal(HttpStatusCode.OK, lockWeek.StatusCode);
        var before = await EntryCountAsync(x.S.Household);
        var kid = x.S.Owner.Client;

        using var add = await AddAsync(kid, x.S.Household, x.Dinner);
        await AssertWeekLockedAsync(add);
        using var change = await kid.PutAsync(Url(x.S.Household, $"/entries/{own}"), new { date = Monday.AddDays(2).ToString("yyyy-MM-dd"), servings = 3 });
        await AssertWeekLockedAsync(change);
        using var remove = await kid.DeleteAsync(Url(x.S.Household, $"/entries/{own}"));
        await AssertWeekLockedAsync(remove);
        using var removeOthers = await kid.DeleteAsync(Url(x.S.Household, $"/entries/{othersPick}")); // week-locked comes before the owner check
        await AssertWeekLockedAsync(removeOthers);
        Assert.Equal(before, await EntryCountAsync(x.S.Household));

        // A real Child member outside kid mode gets the same.
        using var childAdd = await AddAsync(x.Child.Client, x.S.Household, x.Dinner);
        await AssertWeekLockedAsync(childAdd);

        // After unlock the same calls work.
        using var unlock = await x.Admin.DeleteAsync(Url(x.S.Household, "/lock"));
        Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode);
        using var addAgain = await AddAsync(kid, x.S.Household, x.Dinner);
        Assert.Equal(HttpStatusCode.Created, addAgain.StatusCode);
        using var changeAgain = await kid.PutAsync(Url(x.S.Household, $"/entries/{own}"), new { date = Monday.AddDays(2).ToString("yyyy-MM-dd"), servings = 3 });
        Assert.Equal(HttpStatusCode.OK, changeAgain.StatusCode);
        using var removeAgain = await kid.DeleteAsync(Url(x.S.Household, $"/entries/{own}"));
        Assert.Equal(HttpStatusCode.NoContent, removeAgain.StatusCode);
    }

    /// <summary>Makes a pick as the profile on a separate kid device and returns the entry. Leaves <c>Owner.Client</c> in kid mode as that profile.</summary>
    private static async Task<Guid> ProfilePickAsync(AuthHost host, Setup x, Guid profile)
    {
        await x.S.Owner.Client.EnterKidModeAsync(x.S.Household, profile);
        return await AddOkAsync(x.S.Owner.Client, x.S.Household, x.Dinner);
    }

    [Fact]
    public async Task Adult_can_edit_locked_week()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        var existing = await AddOkAsync(x.Admin, x.S.Household, x.Dinner);
        using var lockWeek = await x.Admin.PutAsync(Url(x.S.Household, "/lock"));
        Assert.Equal(HttpStatusCode.OK, lockWeek.StatusCode);

        foreach (var user in new[] { x.Admin, x.Adult.Client })
        {
            var id = await AddOkAsync(user, x.S.Household, x.Dessert);
            using var changed = await user.PutAsync(Url(x.S.Household, $"/entries/{id}"), new { date = Monday.AddDays(3).ToString("yyyy-MM-dd"), servings = 5 });
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            using var removed = await user.DeleteAsync(Url(x.S.Household, $"/entries/{id}"));
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using var removeExisting = await x.Adult.Client.DeleteAsync(Url(x.S.Household, $"/entries/{existing}"));
        Assert.Equal(HttpStatusCode.NoContent, removeExisting.StatusCode);
        using var read = await x.Admin.GetAsync(Url(x.S.Household));
        Assert.True((await read.JsonAsync()).GetProperty("locked").GetBoolean()); // still locked
    }

    [Fact]
    public async Task Lock_is_per_week_and_household()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        var other = await NewAsync(host);
        await x.S.Owner.Client.EnterKidModeAsync(x.S.Household, x.S.Profile1);
        await other.S.Owner.Client.EnterKidModeAsync(other.S.Household, other.S.Profile1);
        using var lockWeek = await x.Admin.PutAsync(Url(x.S.Household, "/lock"));
        Assert.Equal(HttpStatusCode.OK, lockWeek.StatusCode);

        using var sameWeek = await AddAsync(x.S.Owner.Client, x.S.Household, x.Dinner);
        await AssertWeekLockedAsync(sameWeek);
        using var nextWeek = await AddAsync(x.S.Owner.Client, x.S.Household, x.Dinner, Monday.AddDays(7), "2026-11-02");
        Assert.Equal(HttpStatusCode.Created, nextWeek.StatusCode);
        using var otherHousehold = await AddAsync(other.S.Owner.Client, other.S.Household, other.Dinner);
        Assert.Equal(HttpStatusCode.Created, otherHousehold.StatusCode);
        using var otherRead = await other.Admin.GetAsync(Url(other.S.Household));
        Assert.False((await otherRead.JsonAsync()).GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task Lock_visible_in_reads()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        var other = await NewAsync(host);

        using var missing = await x.Admin.GetAsync(Url(x.S.Household));
        var empty = await missing.JsonAsync();
        Assert.False(empty.GetProperty("locked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("lockedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("lockedBy").ValueKind);

        await AddOkAsync(x.Admin, x.S.Household, x.Dinner);
        using var unlocked = await x.Child.Client.GetAsync(Url(x.S.Household));
        Assert.False((await unlocked.JsonAsync()).GetProperty("locked").GetBoolean());

        using var lockWeek = await x.Adult.Client.PutAsync(Url(x.S.Household, "/lock"));
        foreach (var viewer in new[] { x.Child.Client, x.Admin })
        {
            using var read = await viewer.GetAsync(Url(x.S.Household));
            var body = await read.JsonAsync();
            Assert.True(body.GetProperty("locked").GetBoolean());
            Assert.Equal(host.Time.Now, body.GetProperty("lockedAt").GetDateTimeOffset());
            Assert.Equal(x.Adult.User.Id, body.GetProperty("lockedBy").GetProperty("userId").GetGuid());
        }

        // The other household's same week is not locked and not reachable from here.
        using var otherRead = await other.Admin.GetAsync(Url(other.S.Household));
        Assert.False((await otherRead.JsonAsync()).GetProperty("locked").GetBoolean());
        using var intoOther = await x.Admin.GetAsync(Url(other.S.Household));
        Assert.Equal(HttpStatusCode.Forbidden, intoOther.StatusCode);
    }

    [Fact]
    public async Task Lock_race_is_settled()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        await AddOkAsync(x.Admin, x.S.Household, x.Dinner, Monday.AddDays(4)); // creates the plan row
        var planId = (Guid)(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT Id FROM MealPlans WHERE HouseholdId = @h", ("@h", x.S.Household)))!;
        await x.S.Owner.Client.EnterKidModeAsync(x.S.Household, x.S.Profile1);
        var entries = await EntryCountAsync(x.S.Household);

        // 1) The lock commits while a kid add waits on the plan row: the add gets week-locked and writes nothing.
        await using (var held = await HoldPlanRowAsync(planId))
        {
            var kidAdd = AddAsync(x.S.Owner.Client, x.S.Household, x.Dinner, Monday.AddDays(1));
            await Task.Delay(1000, TestContext.Current.CancellationToken);
            Assert.False(kidAdd.IsCompleted, "the kid write should wait for the plan row");
            await held.LockAsync(x.Adult.User.Id);
            await held.CommitAsync();
            using var response = await kidAdd;
            await AssertWeekLockedAsync(response);
        }

        Assert.Equal(entries, await EntryCountAsync(x.S.Household));
        using var unlock = await x.Admin.DeleteAsync(Url(x.S.Household, "/lock"));
        Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode);

        // 2) The add commits first: 201, and the lock that follows holds.
        await using (var held = await HoldPlanRowAsync(planId))
        {
            var kidAdd = AddAsync(x.S.Owner.Client, x.S.Household, x.Dinner, Monday.AddDays(2));
            await Task.Delay(1000, TestContext.Current.CancellationToken);
            Assert.False(kidAdd.IsCompleted);
            await held.CommitAsync();
            using var response = await kidAdd;
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var lockWeek = await x.Admin.PutAsync(Url(x.S.Household, "/lock"));
        Assert.Equal(HttpStatusCode.OK, lockWeek.StatusCode);
        using var after = await AddAsync(x.S.Owner.Client, x.S.Household, x.Dinner, Monday.AddDays(3));
        await AssertWeekLockedAsync(after);
        Assert.Equal(entries + 1, await EntryCountAsync(x.S.Household));
    }

    /// <summary>A transaction that holds the plan row's lock until committed, standing for a concurrent writer.</summary>
    private async Task<HeldPlanRow> HoldPlanRowAsync(Guid planId)
    {
        var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE MealPlans SET UpdatedAt = UpdatedAt WHERE Id = @p";
            command.Parameters.AddWithValue("@p", planId);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return new HeldPlanRow(connection, transaction, planId);
    }

    private sealed class HeldPlanRow(SqlConnection connection, SqlTransaction transaction, Guid planId) : IAsyncDisposable
    {
        public async Task LockAsync(Guid userId)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE MealPlans SET LockedAt = SYSDATETIMEOFFSET(), LockedByUserId = @u WHERE Id = @p";
            command.Parameters.AddWithValue("@u", userId);
            command.Parameters.AddWithValue("@p", planId);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        public Task CommitAsync() => transaction.CommitAsync(TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Lock_changes_are_broadcast_after_the_save_and_a_failing_notifier_does_not_undo_the_change()
    {
        var notifier = new RecordingMealPlanNotifier();
        await using var host = Host(services =>
        {
            services.RemoveAll<IMealPlanNotifier>();
            services.AddSingleton<IMealPlanNotifier>(notifier);
        });
        var x = await NewAsync(host);

        var entry = await AddOkAsync(x.Admin, x.S.Household, x.Dinner);
        using var change = await x.Admin.PutAsync(Url(x.S.Household, $"/entries/{entry}"), new { date = Monday.AddDays(1).ToString("yyyy-MM-dd"), servings = 2 });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        using var remove = await x.Admin.DeleteAsync(Url(x.S.Household, $"/entries/{entry}"));
        using var lockWeek = await x.Admin.PutAsync(Url(x.S.Household, "/lock"));
        using var lockAgain = await x.Admin.PutAsync(Url(x.S.Household, "/lock")); // no change: no signal
        using var unlock = await x.Admin.DeleteAsync(Url(x.S.Household, "/lock"));
        using var unlockAgain = await x.Admin.DeleteAsync(Url(x.S.Household, "/lock"));

        lock (notifier.Changes)
        {
            Assert.Equal(
                [MealPlanChangeKind.EntryAdded, MealPlanChangeKind.EntryChanged, MealPlanChangeKind.EntryRemoved, MealPlanChangeKind.Locked, MealPlanChangeKind.Unlocked],
                notifier.Changes.Select(c => c.Kind));
            Assert.All(notifier.Changes, c =>
            {
                Assert.Equal(x.S.Household, c.HouseholdId);
                Assert.Equal(Monday, c.WeekStart);
            });
            Assert.Equal([entry, entry, entry], notifier.Changes.Take(3).Select(c => c.EntryId!.Value));
            Assert.Null(notifier.Changes[3].EntryId);
            Assert.Null(notifier.Changes[4].EntryId);
        }

        // A failing notifier: the lock is saved anyway.
        notifier.Fail = true;
        using var lockedDespiteFailure = await x.Admin.PutAsync(Url(x.S.Household, "/lock"));
        Assert.Equal(HttpStatusCode.OK, lockedDespiteFailure.StatusCode);
        using var read = await x.Admin.GetAsync(Url(x.S.Household));
        Assert.True((await read.JsonAsync()).GetProperty("locked").GetBoolean());
        using var addDespiteFailure = await AddAsync(x.Adult.Client, x.S.Household, x.Dinner);
        Assert.Equal(HttpStatusCode.Created, addDespiteFailure.StatusCode);
    }

    [Fact]
    public async Task Plan_changes_reach_the_household_hub_group_only()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        var other = await NewAsync(host);
        var events = Channel.CreateUnbounded<JsonElement>();
        var otherEvents = Channel.CreateUnbounded<JsonElement>();

        await using var hub = new HubTestClient(host, x.Admin.AuthCookie);
        hub.Connection.On<JsonElement>("MealPlanChanged", message => events.Writer.TryWrite(message));
        await hub.StartAsync();
        await hub.JoinAsync(x.S.Household);
        await using var foreignHub = new HubTestClient(host, other.Admin.AuthCookie);
        foreignHub.Connection.On<JsonElement>("MealPlanChanged", message => otherEvents.Writer.TryWrite(message));
        await foreignHub.StartAsync();
        await foreignHub.JoinAsync(other.S.Household);

        var entry = await AddOkAsync(x.Adult.Client, x.S.Household, x.Dinner);
        using var lockWeek = await x.Admin.PutAsync(Url(x.S.Household, "/lock"));

        var added = await ReadAsync(events);
        Assert.Equal("EntryAdded", added.GetProperty("kind").GetString());
        Assert.Equal(entry, added.GetProperty("entryId").GetGuid());
        Assert.Equal(Week, added.GetProperty("weekStart").GetString());
        Assert.Equal(x.S.Household, added.GetProperty("householdId").GetGuid());
        var locked = await ReadAsync(events);
        Assert.Equal("Locked", locked.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, locked.GetProperty("entryId").ValueKind);
        Assert.False(otherEvents.Reader.TryRead(out _)); // the other household's client got nothing
        Assert.False(events.Reader.TryRead(out _)); // exactly one signal per change
    }

    private static async Task<JsonElement> ReadAsync(Channel<JsonElement> channel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await channel.Reader.ReadAsync(timeout.Token);
    }

    [Fact]
    public async Task Kid_cannot_remove_others_picks_by_default()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        var byAdult = await AddOkAsync(x.Adult.Client, x.S.Household, x.Dinner);
        var bySecondKid = await ProfilePickAsync(host, x, x.S.Profile2);
        await x.S.Owner.Client.EnterKidModeAsync(x.S.Household, x.S.Profile1);

        foreach (var entry in new[] { byAdult, bySecondKid })
        {
            using var remove = await x.S.Owner.Client.DeleteAsync(Url(x.S.Household, $"/entries/{entry}"));
            Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        }

        Assert.Equal(2, await EntryCountAsync(x.S.Household));
    }

    [Fact]
    public async Task Kid_can_change_only_own_profile_picks()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        var byParentOutsideKidMode = await AddOkAsync(x.Admin, x.S.Household, x.Dinner); // same account, no profile
        var byChild = await AddOkAsync(x.Child.Client, x.S.Household, x.Dinner);
        var bySecondKid = await ProfilePickAsync(host, x, x.S.Profile2);
        var ownPick = await ProfilePickAsync(host, x, x.S.Profile1);
        var tuesday = Monday.AddDays(1).ToString("yyyy-MM-dd");
        var kid = x.S.Owner.Client;

        using var changeOwn = await kid.PutAsync(Url(x.S.Household, $"/entries/{ownPick}"), new { date = tuesday, servings = 3 });
        Assert.Equal(HttpStatusCode.OK, changeOwn.StatusCode);
        Assert.Equal(3, (await changeOwn.JsonAsync()).GetProperty("servings").GetInt32());
        foreach (var other in new[] { bySecondKid, byParentOutsideKidMode })
        {
            using var change = await kid.PutAsync(Url(x.S.Household, $"/entries/{other}"), new { date = tuesday, servings = 3 });
            Assert.Equal(HttpStatusCode.Forbidden, change.StatusCode);
            using var remove = await kid.DeleteAsync(Url(x.S.Household, $"/entries/{other}"));
            Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        }

        using var removeOwn = await kid.DeleteAsync(Url(x.S.Household, $"/entries/{ownPick}"));
        Assert.Equal(HttpStatusCode.NoContent, removeOwn.StatusCode);

        // Outside kid mode a real Child member still changes only what they picked, and an Adult anything.
        using var childOwn = await x.Child.Client.PutAsync(Url(x.S.Household, $"/entries/{byChild}"), new { date = tuesday, servings = 2 });
        Assert.Equal(HttpStatusCode.OK, childOwn.StatusCode);
        using var childOthers = await x.Child.Client.PutAsync(Url(x.S.Household, $"/entries/{bySecondKid}"), new { date = tuesday, servings = 2 });
        Assert.Equal(HttpStatusCode.Forbidden, childOthers.StatusCode);
        using var adultAny = await x.Adult.Client.PutAsync(Url(x.S.Household, $"/entries/{bySecondKid}"), new { date = tuesday, servings = 2 });
        Assert.Equal(HttpStatusCode.OK, adultAny.StatusCode);
    }
}
