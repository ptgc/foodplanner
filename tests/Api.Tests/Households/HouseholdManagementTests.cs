using System.Net;
using System.Text.Json;
using Foodplanner.Api.Realtime;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.ShoppingLists;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Storage;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foodplanner.Api.Tests.Households;

/// <summary>An in-memory image store that records which prefixes were deleted, and can be made to fail.</summary>
internal sealed class RecordingImageStore : IImageStore
{
    public List<string> DeletedPrefixes { get; } = [];

    public bool FailDeletes { get; set; }

    /// <summary>Called while the delete runs, so a test can look at the database at that moment.</summary>
    public Func<Task>? OnDelete { get; set; }

    public Task PutAsync(string name, byte[] content, string contentType, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<Stream?> OpenReadAsync(string name, CancellationToken cancellationToken) => Task.FromResult<Stream?>(null);

    public async Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        DeletedPrefixes.Add(prefix);
        if (OnDelete is not null)
        {
            await OnDelete();
        }

        if (FailDeletes)
        {
            throw new IOException("storage is down");
        }
    }
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Entries)
            {
                owner.Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}

/// <summary>Leave, remove, delete and export (T-044), including the live-connection eviction of Q-015.</summary>
public sealed class HouseholdManagementTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AuthHost Host(RecordingImageStore? store = null, CapturingLoggerProvider? logs = null) =>
        AuthHost.Create(
            db.ConnectionString,
            configureServices: services =>
            {
                if (store is not null)
                {
                    services.AddSingleton<IImageStore>(store);
                }

                if (logs is not null)
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                }
            });

    private TenantIsolationEnvironment Env => new(db.ConnectionString);

    private static string HouseholdUrl(Guid household, string rest = "") => $"/api/households/{household}{rest}";

    private static IEnumerable<IEntityType> HouseholdOwnedTypes() =>
        TenantIsolationEnvironment.ProductionModel().GetEntityTypes()
            .Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType) || typeof(ICatalogEntity).IsAssignableFrom(t.ClrType))
            .OrderBy(t => t.ClrType.Name, StringComparer.Ordinal);

    /// <summary>One row of EVERY household-owned entity type, from the isolation registry. Fails loudly for a type without a factory.</summary>
    private async Task SeedEverythingAsync(Guid household)
    {
        var types = HouseholdOwnedTypes().Select(t => t.ClrType).ToList();
        Assert.NotEmpty(types);
        var withoutFactory = types.Where(t => TenantIsolationRegistry.Cases.All(c => c.EntityType != t)).Select(t => t.Name).ToList();
        Assert.True(
            withoutFactory.Count == 0,
            $"These household-owned entities have no TenantIsolationRegistry factory, so deletion and export cannot be proven for them: {string.Join(", ", withoutFactory)}");

        foreach (var type in types)
        {
            await TenantIsolationRegistry.Cases.Single(c => c.EntityType == type).SeedRowAsync(Env, household);
        }
    }

    private static string Table(IEntityType type) => $"[{type.GetSchema() ?? "dbo"}].[{type.GetTableName()}]";

    private async Task<int> CountAsync(IEntityType type, Guid household) =>
        Convert.ToInt32(
            await HouseholdScenario.ScalarAsync(db.ConnectionString, $"SELECT COUNT(*) FROM {Table(type)} WHERE HouseholdId = @h", ("@h", household)));

    private async Task<Dictionary<string, int>> CountsAsync(Guid household)
    {
        var counts = new Dictionary<string, int>();
        foreach (var type in HouseholdOwnedTypes())
        {
            counts[type.ClrType.Name] = await CountAsync(type, household);
        }

        return counts;
    }

    private async Task<List<Guid>> IdsAsync(IEntityType type, Guid household)
    {
        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id FROM {Table(type)} WHERE HouseholdId = @h ORDER BY Id";
        command.Parameters.AddWithValue("@h", household);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private async Task<bool> HouseholdExistsAsync(Guid household) =>
        Convert.ToInt32(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM Households WHERE Id = @h", ("@h", household))) == 1;

    private async Task<int> MembershipCountAsync(Guid household, Guid user) =>
        Convert.ToInt32(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM Memberships WHERE HouseholdId = @h AND UserId = @u", ("@h", household), ("@u", user)));

    private Task MakeOwnerAsync(Guid household, Guid user) =>
        HouseholdScenario.ScalarAsync(
            db.ConnectionString, "UPDATE Memberships SET Role = 'Owner' WHERE HouseholdId = @h AND UserId = @u; SELECT @@ROWCOUNT", ("@h", household), ("@u", user));

    // ---- delete -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_household_removes_every_row_of_every_tenant_entity_and_touches_nothing_of_another_household()
    {
        await using var host = Host(new RecordingImageStore());
        var ownerA = await host.SignInAsync();
        var a = await ownerA.Client.CreateHouseholdAsync("A");
        var ownerB = await host.SignInAsync();
        var b = await ownerB.Client.CreateHouseholdAsync("B");
        await SeedEverythingAsync(a);
        await SeedEverythingAsync(b);
        var before = await CountsAsync(a);
        var beforeB = await CountsAsync(b);
        // Not vacuous: every type has at least one row in both households (the factories seeded them).
        Assert.All(before, kv => Assert.True(kv.Value >= 1, $"{kv.Key} has no row in A"));
        Assert.All(beforeB, kv => Assert.True(kv.Value >= 1, $"{kv.Key} has no row in B"));
        var idsB = new Dictionary<string, List<Guid>>();
        foreach (var type in HouseholdOwnedTypes())
        {
            idsB[type.ClrType.Name] = await IdsAsync(type, b);
        }

        var userRows = Convert.ToInt32(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM AspNetUsers"));

        using var deleted = await ownerA.Client.DeleteAsync(HouseholdUrl(a));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(await HouseholdExistsAsync(a));
        foreach (var type in HouseholdOwnedTypes())
        {
            Assert.True(await CountAsync(type, a) == 0, $"{type.ClrType.Name} still has rows of the deleted household");
            Assert.Equal(idsB[type.ClrType.Name], await IdsAsync(type, b)); // B's rows: same ids, same count
        }

        Assert.Equal(beforeB, await CountsAsync(b));
        Assert.True(await HouseholdExistsAsync(b));
        // People are global identities and survive; only their membership went.
        Assert.Equal(userRows, Convert.ToInt32(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM AspNetUsers")));
        Assert.Empty(await ownerA.Client.ListHouseholdsAsync());
        using var gone = await ownerA.Client.GetAsync(HouseholdUrl(a, "/export"));
        await gone.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        Assert.Single(await ownerB.Client.ListHouseholdsAsync());
    }

    [Fact]
    public async Task Only_the_owner_of_that_household_can_delete_it()
    {
        await using var host = Host(new RecordingImageStore());
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");
        var outsider = await host.SignInAsync();
        var other = await outsider.Client.CreateHouseholdAsync("Other");

        foreach (var caller in new[] { adult, child, outsider })
        {
            using var denied = await caller.Client.DeleteAsync(HouseholdUrl(household));
            await denied.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        // The Owner of another household has no power here, and the other household is not reachable through this route either.
        using var foreign = await outsider.Client.DeleteAsync(HouseholdUrl(household));
        await foreign.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var anonymous = host.CreateClient();
        using var unauthenticated = await anonymous.DeleteAsync(HouseholdUrl(household));
        await unauthenticated.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        using var unknown = await owner.Client.DeleteAsync(HouseholdUrl(Guid.NewGuid()));
        await unknown.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        Assert.True(await HouseholdExistsAsync(household));
        Assert.True(await HouseholdExistsAsync(other));
        Assert.Equal(3, Convert.ToInt32(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM Memberships WHERE HouseholdId = @h", ("@h", household))));
    }

    [Fact]
    public async Task Deleting_a_household_deletes_its_blob_prefix_after_the_commit_and_never_another_households()
    {
        var store = new RecordingImageStore();
        await using var host = Host(store);
        var owner = await host.SignInAsync();
        var a = await owner.Client.CreateHouseholdAsync("A");
        var b = await owner.Client.CreateHouseholdAsync("B");
        bool? householdGoneWhenBlobsDeleted = null;
        store.OnDelete = async () => householdGoneWhenBlobsDeleted = !await HouseholdExistsAsync(a);

        using var deleted = await owner.Client.DeleteAsync(HouseholdUrl(a));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal([$"{a:N}/"], store.DeletedPrefixes);
        Assert.NotEqual($"{a:N}/", $"{b:N}/");
        Assert.True(householdGoneWhenBlobsDeleted); // storage is touched only after the database delete committed
    }

    [Fact]
    public async Task A_storage_failure_after_the_delete_is_logged_and_does_not_fail_the_request()
    {
        var store = new RecordingImageStore { FailDeletes = true };
        var logs = new CapturingLoggerProvider();
        await using var host = Host(store, logs);
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var deleted = await owner.Client.DeleteAsync(HouseholdUrl(household));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(await HouseholdExistsAsync(household));
        lock (logs.Entries)
        {
            Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains($"{household:N}/", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Deleting_a_household_closes_every_live_connection_that_joined_it()
    {
        await using var host = Host(new RecordingImageStore());
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var elsewhere = await host.SignInAsync();
        await elsewhere.Client.CreateHouseholdAsync("Elsewhere");
        await using var ownerHub = new HubTestClient(host, owner.Client.AuthCookie);
        await using var adultHub = new HubTestClient(host, adult.Client.AuthCookie);
        await using var otherHub = new HubTestClient(host, elsewhere.Client.AuthCookie);
        foreach (var hub in new[] { ownerHub, adultHub, otherHub })
        {
            await hub.StartAsync();
        }

        await ownerHub.JoinAsync(household);
        await adultHub.JoinAsync(household);

        using var deleted = await owner.Client.DeleteAsync(HouseholdUrl(household));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.True(await ownerHub.ClosesWithinAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await adultHub.ClosesWithinAsync(TimeSpan.FromSeconds(10)));
        Assert.False(await otherHub.ClosesWithinAsync(TimeSpan.FromSeconds(1))); // unrelated connections stay
    }

    // ---- leave ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Adult")]
    [InlineData("Child")]
    public async Task A_member_can_leave_and_loses_access_but_keeps_the_account_and_other_households(string role)
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var member = await host.JoinAsync(owner, household, role);
        var own = await member.Client.CreateHouseholdAsync("Own");

        using var left = await member.Client.PostAsync(HouseholdUrl(household, "/leave"));

        Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        Assert.Equal(0, await MembershipCountAsync(household, member.User.Id));
        Assert.Equal([own], (await member.Client.ListHouseholdsAsync()).Select(h => h.Id).ToList());
        using var denied = await member.Client.GetAsync(HouseholdUrl(household, "/tags"));
        await denied.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(1, await MembershipCountAsync(household, owner.User.Id));
        Assert.True(await HouseholdExistsAsync(household));
        using var again = await member.Client.PostAsync(HouseholdUrl(household, "/leave"));
        await again.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden"); // no longer a member
    }

    [Fact]
    public async Task The_last_owner_cannot_leave_while_another_owner_can()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var second = await host.JoinAsync(owner, household, "Adult");

        using var refused = await owner.Client.PostAsync(HouseholdUrl(household, "/leave"));

        await refused.AssertProblemAsync(HttpStatusCode.Conflict, "last-owner");
        Assert.Equal(1, await MembershipCountAsync(household, owner.User.Id));

        await MakeOwnerAsync(household, second.User.Id); // stands in for ownership transfer (T-048)
        using var allowed = await owner.Client.PostAsync(HouseholdUrl(household, "/leave"));
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal(0, await MembershipCountAsync(household, owner.User.Id));

        using var nowLast = await second.Client.PostAsync(HouseholdUrl(household, "/leave"));
        await nowLast.AssertProblemAsync(HttpStatusCode.Conflict, "last-owner");
        Assert.Equal(1, await MembershipCountAsync(household, second.User.Id));
    }

    [Fact]
    public async Task Two_owners_leaving_at_once_never_leave_the_household_without_an_owner()
    {
        await using var host = Host();
        for (var round = 0; round < 3; round++)
        {
            var first = await host.SignInAsync();
            var household = await first.Client.CreateHouseholdAsync();
            var second = await host.JoinAsync(first, household, "Adult");
            await MakeOwnerAsync(household, second.User.Id);

            var results = await Task.WhenAll(
                first.Client.PostAsync(HouseholdUrl(household, "/leave")),
                second.Client.PostAsync(HouseholdUrl(household, "/leave")));
            try
            {
                Assert.Equal(
                    [HttpStatusCode.NoContent, HttpStatusCode.Conflict],
                    results.Select(r => r.StatusCode).Order().ToList());
                Assert.Equal(1, Convert.ToInt32(await HouseholdScenario.ScalarAsync(
                    db.ConnectionString, "SELECT COUNT(*) FROM Memberships WHERE HouseholdId = @h AND Role = 'Owner'", ("@h", household))));
            }
            finally
            {
                foreach (var result in results)
                {
                    result.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task A_former_owners_open_invites_stop_working_when_they_leave_but_accepted_ones_stay()
    {
        await using var host = Host();
        var founder = await host.SignInAsync();
        var household = await founder.Client.CreateHouseholdAsync();
        var coOwner = await host.JoinAsync(founder, household, "Adult");
        await MakeOwnerAsync(household, coOwner.User.Id);
        var openToken = await coOwner.Client.CreateInviteAsync(household, "Adult");
        var usedToken = await coOwner.Client.CreateInviteAsync(household, "Child");
        var guest = await host.SignInAsync();
        using (var accepted = await guest.Client.AcceptAsync(usedToken))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var left = await coOwner.Client.PostAsync(HouseholdUrl(household, "/leave"));
        Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);

        var latecomer = await host.SignInAsync();
        using var rejected = await latecomer.Client.AcceptAsync(openToken);
        await rejected.AssertProblemAsync(HttpStatusCode.Gone, "invite-invalid");
        Assert.Equal(1, await MembershipCountAsync(household, guest.User.Id)); // who they invited stays
        Assert.Equal(3, Convert.ToInt32(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM Invites WHERE HouseholdId = @h", ("@h", household)))); // rows are kept: the co-owner join invite and the two created
    }

    // ---- remove -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_owner_removes_a_member_who_then_has_no_access_but_keeps_the_account()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");

        using var removed = await owner.Client.DeleteAsync(HouseholdUrl(household, $"/members/{adult.User.Id}"));

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0, await MembershipCountAsync(household, adult.User.Id));
        Assert.Equal(1, await MembershipCountAsync(household, child.User.Id));
        using var denied = await adult.Client.GetAsync(HouseholdUrl(household, "/tags"));
        await denied.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        Assert.Empty(await adult.Client.ListHouseholdsAsync());
        Assert.Equal(1, Convert.ToInt32(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM AspNetUsers WHERE Id = @u", ("@u", adult.User.Id))));
        using var again = await owner.Client.DeleteAsync(HouseholdUrl(household, $"/members/{adult.User.Id}"));
        await again.AssertProblemAsync(HttpStatusCode.NotFound, "member-not-found");
    }

    [Fact]
    public async Task Remove_member_is_owner_only_and_refuses_self_owners_strangers_and_other_households()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");
        var coOwner = await host.JoinAsync(owner, household, "Adult");
        await MakeOwnerAsync(household, coOwner.User.Id);
        var otherOwner = await host.SignInAsync();
        var otherHousehold = await otherOwner.Client.CreateHouseholdAsync("Other");
        var otherMember = await host.JoinAsync(otherOwner, otherHousehold, "Adult");

        // Adult and Child cannot remove anyone, not even each other or themselves through this route.
        using var byAdult = await adult.Client.DeleteAsync(HouseholdUrl(household, $"/members/{child.User.Id}"));
        await byAdult.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var byChild = await child.Client.DeleteAsync(HouseholdUrl(household, $"/members/{adult.User.Id}"));
        await byChild.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var anonymous = await host.CreateClient().DeleteAsync(HouseholdUrl(household, $"/members/{child.User.Id}"));
        await anonymous.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");

        // The Owner cannot remove themselves (use leave), another Owner, an unknown user, or a member of another household.
        using var self = await owner.Client.DeleteAsync(HouseholdUrl(household, $"/members/{owner.User.Id}"));
        await self.AssertProblemAsync(HttpStatusCode.Conflict, "cannot-remove-self");
        using var other = await owner.Client.DeleteAsync(HouseholdUrl(household, $"/members/{coOwner.User.Id}"));
        await other.AssertProblemAsync(HttpStatusCode.Conflict, "owner-cannot-be-removed");
        using var unknown = await owner.Client.DeleteAsync(HouseholdUrl(household, $"/members/{Guid.NewGuid()}"));
        await unknown.AssertProblemAsync(HttpStatusCode.NotFound, "member-not-found");
        using var foreign = await owner.Client.DeleteAsync(HouseholdUrl(household, $"/members/{otherMember.User.Id}"));
        await foreign.AssertProblemAsync(HttpStatusCode.NotFound, "member-not-found");
        // And the Owner of one household cannot reach into another.
        using var wrongHousehold = await owner.Client.DeleteAsync(HouseholdUrl(otherHousehold, $"/members/{otherMember.User.Id}"));
        await wrongHousehold.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        Assert.Equal(1, await MembershipCountAsync(household, owner.User.Id));
        Assert.Equal(1, await MembershipCountAsync(household, adult.User.Id));
        Assert.Equal(1, await MembershipCountAsync(household, child.User.Id));
        Assert.Equal(1, await MembershipCountAsync(household, coOwner.User.Id));
        Assert.Equal(1, await MembershipCountAsync(otherHousehold, otherMember.User.Id));
    }

    // ---- eviction of live connections (Q-015) -----------------------------------------------------------------

    [Fact]
    public async Task A_removed_member_is_evicted_from_the_group_and_their_connection_is_closed()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var stays = await host.JoinAsync(owner, household, "Child");
        await using var ownerHub = new HubTestClient(host, owner.Client.AuthCookie);
        await using var removedHub = new HubTestClient(host, adult.Client.AuthCookie);
        await using var staysHub = new HubTestClient(host, stays.Client.AuthCookie);
        foreach (var hub in new[] { ownerHub, removedHub, staysHub })
        {
            await hub.StartAsync();
            await hub.JoinAsync(household);
        }

        using var removed = await owner.Client.DeleteAsync(HouseholdUrl(household, $"/members/{adult.User.Id}"));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        Assert.True(await removedHub.ClosesWithinAsync(TimeSpan.FromSeconds(10)));
        // Everyone else is untouched and still gets the household's signals; the removed connection gets none.
        using var added = await owner.Client.PostAsync(
            $"/api/households/{household}/shopping-lists/2026-10-26/items", new { label = "After removal" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal(household, (await ownerHub.NextAsync()).HouseholdId);
        Assert.Equal(household, (await staysHub.NextAsync()).HouseholdId);
        Assert.False(await staysHub.ClosesWithinAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Disconnected, removedHub.Connection.State);

        // A fresh connection of the removed member connects (the cookie is fine) but cannot join or read.
        await using var again = new HubTestClient(host, adult.Client.AuthCookie);
        await again.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => again.JoinAsync(household));
    }

    [Fact]
    public async Task Leaving_closes_the_leavers_connection_that_joined_but_not_one_of_another_household()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var member = await host.JoinAsync(owner, household, "Adult");
        var own = await member.Client.CreateHouseholdAsync("Own");
        await using var joined = new HubTestClient(host, member.Client.AuthCookie);
        await using var elsewhere = new HubTestClient(host, member.Client.AuthCookie);
        await joined.StartAsync();
        await elsewhere.StartAsync();
        await joined.JoinAsync(household);
        await elsewhere.JoinAsync(own);

        using var left = await member.Client.PostAsync(HouseholdUrl(household, "/leave"));

        Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        Assert.True(await joined.ClosesWithinAsync(TimeSpan.FromSeconds(10)));
        Assert.False(await elsewhere.ClosesWithinAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_security_stamp_change_closes_all_of_that_users_connections_and_only_theirs()
    {
        await using var host = Host();
        var user = await host.SignInAsync();
        var household = await user.Client.CreateHouseholdAsync();
        var bystander = await host.SignInAsync();
        await bystander.Client.CreateHouseholdAsync("Bystander");
        await using var first = new HubTestClient(host, user.Client.AuthCookie);
        await using var second = new HubTestClient(host, user.Client.AuthCookie);
        await using var unrelated = new HubTestClient(host, bystander.Client.AuthCookie);
        await first.StartAsync();
        await second.StartAsync();
        await unrelated.StartAsync();
        await first.JoinAsync(household); // one joined, one only connected: both go

        await host.Services.GetRequiredService<ILiveConnections>().CloseUserAsync(user.User.Id);

        Assert.True(await first.ClosesWithinAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await second.ClosesWithinAsync(TimeSpan.FromSeconds(10)));
        Assert.False(await unrelated.ClosesWithinAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task The_endpoints_that_rotate_the_security_stamp_close_the_users_connections()
    {
        await using var host = Host();
        var user = await host.SignInAsync();
        await user.Client.CreateHouseholdAsync();
        await using var resend = new HubTestClient(host, user.Client.AuthCookie);
        await resend.StartAsync();

        // The account becomes unconfirmed again (a state the T-072 flow handles); registering the address rotates its stamp.
        await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "UPDATE AspNetUsers SET EmailConfirmed = 0, RegisteredAt = NULL WHERE Id = @u; SELECT @@ROWCOUNT", ("@u", user.User.Id));
        using (var anonymous = host.CreateClient())
        using (var registered = await anonymous.PostAsync("/api/auth/register", new { email = user.User.Email }))
        {
            Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        }

        Assert.True(await resend.ClosesWithinAsync(TimeSpan.FromSeconds(10)));

        // Confirming with the emailed link rotates it again.
        await using var confirm = new HubTestClient(host, user.Client.AuthCookie);
        await confirm.StartAsync();
        var mail = host.Email.Messages.Last(m => m.To == user.User.Email && m.TextBody.Contains("set-password", StringComparison.Ordinal)).TextBody;
        var match = System.Text.RegularExpressions.Regex.Match(mail, @"/set-password\?userId=(?<id>[0-9a-fA-F-]{36})&token=(?<token>\S+)");
        Assert.True(match.Success);
        using (var anonymous = host.CreateClient())
        using (var confirmed = await anonymous.PostAsync(
            "/api/auth/confirm-email",
            new { userId = match.Groups["id"].Value, token = Uri.UnescapeDataString(match.Groups["token"].Value), password = "a brand new password 123" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        }

        Assert.True(await confirm.ClosesWithinAsync(TimeSpan.FromSeconds(10)));
    }

    // ---- export -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Export_contains_every_row_of_every_tenant_entity_of_the_household_and_nothing_of_another()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var a = await owner.Client.CreateHouseholdAsync("Export A");
        var adult = await host.JoinAsync(owner, a, "Adult");
        var ownerB = await host.SignInAsync();
        var b = await ownerB.Client.CreateHouseholdAsync("Export B");
        await SeedEverythingAsync(a);
        await SeedEverythingAsync(b);
        var recipeA = (await IdsAsync(HouseholdOwnedTypes().Single(t => t.ClrType.Name == "Recipe"), a))[0];
        var version = Guid.NewGuid();
        await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "UPDATE Recipes SET ImageVersion = @v WHERE Id = @r; SELECT @@ROWCOUNT", ("@v", version), ("@r", recipeA));

        using var response = await owner.Client.GetAsync(HouseholdUrl(a, "/export"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains(a.ToString("N"), response.Content.Headers.ContentDisposition?.FileName, StringComparison.Ordinal);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var export = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("foodplanner-household-export", export.GetProperty("format").GetString());
        Assert.Equal(a, export.GetProperty("household").GetProperty("id").GetGuid());
        Assert.Equal("Export A", export.GetProperty("household").GetProperty("name").GetString());

        var entities = export.GetProperty("entities");
        var expectedTypes = HouseholdOwnedTypes().Select(t => t.ClrType.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expectedTypes, entities.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList());
        foreach (var type in HouseholdOwnedTypes())
        {
            var rows = entities.GetProperty(type.ClrType.Name).EnumerateArray().ToList();
            Assert.NotEmpty(rows);
            Assert.Equal((await IdsAsync(type, a)).Order().ToList(), rows.Select(r => r.GetProperty("id").GetGuid()).Order().ToList());
            Assert.All(rows, row =>
            {
                Assert.Equal(a, row.GetProperty("householdId").GetGuid());
                Assert.True(row.TryGetProperty("createdAt", out _), $"{type.ClrType.Name} row has no createdAt");
            });
            Assert.Empty(rows.Select(r => r.GetProperty("id").GetGuid()).Intersect(await IdsAsync(type, b)));
        }

        // Nothing of B anywhere in the document, nor any global catalog row, nor a secret.
        Assert.DoesNotContain(b.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ownerB.User.Email, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rowVersion", raw, StringComparison.OrdinalIgnoreCase);
        var ingredientIds = entities.GetProperty("Ingredient").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Empty(ingredientIds.Intersect(IngredientSeed.All.Select(i => i.Id)));

        // Members come with their address; the invite carries no token.
        var memberships = entities.GetProperty("Membership").EnumerateArray().ToList();
        Assert.Contains(memberships, m => m.GetProperty("email").GetString() == owner.User.Email && m.GetProperty("role").GetString() == "Owner");
        Assert.Contains(memberships, m => m.GetProperty("email").GetString() == adult.User.Email && m.GetProperty("role").GetString() == "Adult");

        // Pictures by reference, not bytes.
        var image = Assert.Single(export.GetProperty("images").EnumerateArray());
        Assert.Equal(recipeA, image.GetProperty("recipeId").GetGuid());
        Assert.Equal(version, image.GetProperty("version").GetGuid());
        Assert.Equal($"/api/households/{a}/recipes/{recipeA}/image/{version:N}/full", image.GetProperty("fullUrl").GetString());
    }

    [Fact]
    public async Task Export_is_for_owner_and_adult_only_and_each_household_gets_its_own()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");
        var outsider = await host.SignInAsync();
        await outsider.Client.CreateHouseholdAsync("Elsewhere");

        foreach (var allowed in new[] { owner, adult })
        {
            using var ok = await allowed.Client.GetAsync(HouseholdUrl(household, "/export"));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        foreach (var denied in new[] { child, outsider })
        {
            using var forbidden = await denied.Client.GetAsync(HouseholdUrl(household, "/export"));
            await forbidden.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using var anonymous = await host.CreateClient().GetAsync(HouseholdUrl(household, "/export"));
        await anonymous.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }
}
