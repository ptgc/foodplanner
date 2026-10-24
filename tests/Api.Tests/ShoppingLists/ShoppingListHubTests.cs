using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Foodplanner.Api.Features.ShoppingLists;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Foodplanner.Api.Tests.ShoppingLists;

/// <summary>A hub connection over the test server (real WebSocket transport) that records every pushed event.</summary>
internal sealed class HubTestClient : IAsyncDisposable
{
    private readonly Channel<ShoppingListChangedMessage> _events = Channel.CreateUnbounded<ShoppingListChangedMessage>();

    public HubTestClient(AuthHost host, string? cookie, string? origin = null)
    {
        Connection = new HubConnectionBuilder()
            .WithUrl(new Uri("https://localhost/api/hubs/shopping-list"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
                options.WebSocketFactory = (context, ct) =>
                {
                    var client = host.Server.CreateWebSocketClient();
                    client.ConfigureRequest = request =>
                    {
                        if (cookie is not null)
                        {
                            request.Headers.Cookie = cookie;
                        }

                        if (origin is not null)
                        {
                            request.Headers.Origin = origin;
                        }
                    };
                    return new ValueTask<System.Net.WebSockets.WebSocket>(client.ConnectAsync(context.Uri, ct));
                };
                if (cookie is not null)
                {
                    options.Headers["Cookie"] = cookie;
                }

                if (origin is not null)
                {
                    options.Headers["Origin"] = origin;
                }
            })
            .Build();
        Connection.On<ShoppingListChangedMessage>(ShoppingListHub.ChangedEvent, message => _events.Writer.TryWrite(message));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The hub type is internal; the name is part of the wire contract.
    private static class ShoppingListHub
    {
        public const string ChangedEvent = "ShoppingListChanged";
    }

    public HubConnection Connection { get; }

    public Task StartAsync() => Connection.StartAsync(Ct);

    public Task StopAsync() => Connection.StopAsync(Ct);

    public Task JoinAsync(Guid household) => Connection.InvokeAsync("JoinHousehold", household, Ct);

    public async Task<JsonElement> GetStateAsync(Guid household, DateOnly week) =>
        await Connection.InvokeAsync<JsonElement>("GetState", household, week.ToString("yyyy-MM-dd"), Ct);

    public async Task<ShoppingListChangedMessage> NextAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await _events.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>True when no event arrives within the window.</summary>
    public async Task<bool> StaysSilentAsync(TimeSpan window)
    {
        await Task.Delay(window, Ct);
        // Drain, so a late event cannot leak into a later assertion.
        var any = false;
        while (_events.Reader.TryRead(out _))
        {
            any = true;
        }

        return !any;
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}

public sealed class ShoppingListHubTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly DateOnly Monday = new(2026, 10, 26);

    private AuthHost Host() => HouseholdHost.Create(db.ConnectionString);

    private static string ListUrl(Guid household, string rest = "") =>
        $"/api/households/{household}/shopping-lists/{Monday:yyyy-MM-dd}{rest}";

    private static async Task<Guid> AddManualAsync(ApiClient client, Guid household, string label)
    {
        using var response = await client.PostAsync(ListUrl(household, "/items"), new { label });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task CheckAsync(ApiClient client, Guid household, Guid item, bool isChecked)
    {
        using var response = await client.PutAsync(ListUrl(household, $"/items/{item}/check"), new { @checked = isChecked });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<(SignedInUser User, Guid Household, HubTestClient Hub)> JoinedAsync(AuthHost host)
    {
        var user = await host.SignInAsync();
        var household = await user.Client.CreateHouseholdAsync();
        var hub = new HubTestClient(host, user.Client.AuthCookie);
        await hub.StartAsync();
        await hub.JoinAsync(household);
        return (user, household, hub);
    }

    [Fact]
    public async Task An_anonymous_connection_is_rejected()
    {
        await using var host = Host();
        await using var hub = new HubTestClient(host, cookie: null);

        var failure = await Assert.ThrowsAnyAsync<Exception>(hub.StartAsync);

        Assert.Contains("401", failure.Message, StringComparison.Ordinal);
        Assert.Equal(HubConnectionState.Disconnected, hub.Connection.State);
    }

    [Fact]
    public async Task Negotiate_without_antiforgery_token_works_for_a_signed_in_user_and_is_401_for_anonymous()
    {
        await using var host = Host();
        var user = await host.SignInAsync();

        // The hub is outside the antiforgery root group: negotiate carries no token and still succeeds when signed in.
        using var signedIn = await user.Client.PostWithTokenAsync("/api/hubs/shopping-list/negotiate?negotiateVersion=1", body: null, token: null);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);

        using var anonymous = host.CreateClient();
        using var denied = await anonymous.PostWithTokenAsync("/api/hubs/shopping-list/negotiate?negotiateVersion=1", body: null, token: null);
        await denied.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task A_cross_origin_handshake_is_rejected_and_a_same_origin_one_is_not()
    {
        await using var host = Host();
        var user = await host.SignInAsync();

        await using var foreign = new HubTestClient(host, user.Client.AuthCookie, origin: "https://evil.example");
        var failure = await Assert.ThrowsAnyAsync<Exception>(foreign.StartAsync);
        Assert.Contains("403", failure.Message, StringComparison.Ordinal);

        await using var own = new HubTestClient(host, user.Client.AuthCookie, origin: "https://localhost");
        await own.StartAsync();
        Assert.Equal(HubConnectionState.Connected, own.Connection.State);
    }

    [Fact]
    public async Task Negotiate_origin_rules_null_is_refused_missing_and_other_port_are_accepted_and_refusal_is_problem_details()
    {
        await using var host = Host();
        var user = await host.SignInAsync();
        const string url = "/api/hubs/shopping-list/negotiate?negotiateVersion=1";

        using var foreign = await user.Client.PostAsync(url, null, new Dictionary<string, string> { ["Origin"] = "https://evil.example" });
        await foreign.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden-origin");

        using var opaque = await user.Client.PostAsync(url, null, new Dictionary<string, string> { ["Origin"] = "null" });
        await opaque.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden-origin");

        using var missing = await user.Client.PostAsync(url);
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);

        // Documented decision: the port is not compared (nginx forwards $host without one).
        using var otherPort = await user.Client.PostAsync(url, null, new Dictionary<string, string> { ["Origin"] = "https://localhost:9999" });
        Assert.Equal(HttpStatusCode.OK, otherPort.StatusCode);
    }

    [Fact]
    public async Task One_connection_in_two_households_gets_each_households_own_list_per_call()
    {
        await using var host = Host();
        var user = await host.SignInAsync();
        var a = await user.Client.CreateHouseholdAsync("A");
        var b = await user.Client.CreateHouseholdAsync("B");
        await AddManualAsync(user.Client, a, "Only in A");
        await AddManualAsync(user.Client, b, "Only in B");
        await using var hub = new HubTestClient(host, user.Client.AuthCookie);
        await hub.StartAsync();

        static string[] Names(JsonElement state) =>
            [.. state.GetProperty("list").GetProperty("main").EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];

        Assert.Equal(["Only in A"], Names(await hub.GetStateAsync(a, Monday)));
        Assert.Equal(["Only in B"], Names(await hub.GetStateAsync(b, Monday)));
        Assert.Equal(["Only in A"], Names(await hub.GetStateAsync(a, Monday)));
    }

    [Fact]
    public async Task After_leaving_one_of_two_joined_households_only_the_other_delivers_events()
    {
        await using var host = Host();
        var user = await host.SignInAsync();
        var a = await user.Client.CreateHouseholdAsync("A");
        var b = await user.Client.CreateHouseholdAsync("B");
        await using var hub = new HubTestClient(host, user.Client.AuthCookie);
        await hub.StartAsync();
        await hub.JoinAsync(a);
        await hub.JoinAsync(b);
        await hub.Connection.InvokeAsync("LeaveHousehold", a, TestContext.Current.CancellationToken);

        await AddManualAsync(user.Client, a, "In A");
        await AddManualAsync(user.Client, b, "In B");

        Assert.Equal(b, (await hub.NextAsync()).HouseholdId);
        Assert.True(await hub.StaysSilentAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_revoked_member_who_already_joined_keeps_getting_content_free_signals_until_disconnect()
    {
        // Pins the documented residual window (docs/domain.md): a joined connection stays in its group.
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var member = await host.JoinAsync(owner, household, "Adult");
        await using var hub = new HubTestClient(host, member.Client.AuthCookie);
        await hub.StartAsync();
        await hub.JoinAsync(household);

        await HouseholdScenario.ScalarAsync(
            db.ConnectionString,
            "DELETE FROM Memberships WHERE HouseholdId = @h AND UserId = @u; SELECT @@ROWCOUNT",
            ("@h", household), ("@u", member.User.Id));
        await AddManualAsync(owner.Client, household, "After revocation");

        var signal = await hub.NextAsync();
        Assert.Equal(ShoppingListChangeKind.ManualItemAdded, signal.Kind); // metadata only
        await Assert.ThrowsAsync<HubException>(() => hub.GetStateAsync(household, Monday)); // content stays closed

        await hub.StopAsync();
        await hub.StartAsync();
        Assert.True(await hub.StaysSilentAsync(TimeSpan.FromSeconds(1)));
        await AddManualAsync(owner.Client, household, "After disconnect");
        await Assert.ThrowsAsync<HubException>(() => hub.JoinAsync(household));
        Assert.True(await hub.StaysSilentAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_non_member_cannot_join_or_read_state()
    {
        await using var host = Host();
        var (_, household, owner) = await JoinedAsync(host);
        await using var ownerHub = owner;
        var outsider = await host.SignInAsync();
        await outsider.Client.CreateHouseholdAsync("Elsewhere");
        await using var hub = new HubTestClient(host, outsider.Client.AuthCookie);
        await hub.StartAsync();

        var join = await Assert.ThrowsAsync<HubException>(() => hub.JoinAsync(household));
        var state = await Assert.ThrowsAsync<HubException>(() => hub.GetStateAsync(household, Monday));
        var unknown = await Assert.ThrowsAsync<HubException>(() => hub.JoinAsync(Guid.NewGuid()));

        Assert.Contains("forbidden", join.Message, StringComparison.Ordinal);
        Assert.Contains("forbidden", state.Message, StringComparison.Ordinal);
        Assert.Equal(join.Message, unknown.Message); // unknown and foreign households look the same
    }

    [Fact]
    public async Task The_household_group_receives_each_kind_of_change_in_order()
    {
        await using var host = Host();
        var (user, household, hubOwner) = await JoinedAsync(host);
        await using var hub = hubOwner;

        using (await user.Client.PostAsync(ListUrl(household, "/generate")))
        {
        }

        var generated = await hub.NextAsync();
        var item = await AddManualAsync(user.Client, household, "Batteries");
        var added = await hub.NextAsync();
        await CheckAsync(user.Client, household, item, true);
        var checkedMessage = await hub.NextAsync();
        await CheckAsync(user.Client, household, item, false);
        var unchecked_ = await hub.NextAsync();
        using (await user.Client.DeleteAsync(ListUrl(household, $"/items/{item}")))
        {
        }

        var removed = await hub.NextAsync();

        Assert.Equal(
            [ShoppingListChangeKind.Generated, ShoppingListChangeKind.ManualItemAdded, ShoppingListChangeKind.ItemChecked,
             ShoppingListChangeKind.ItemUnchecked, ShoppingListChangeKind.ItemRemoved],
            new[] { generated.Kind, added.Kind, checkedMessage.Kind, unchecked_.Kind, removed.Kind });
        Assert.All(new[] { generated, added, checkedMessage, unchecked_, removed }, m =>
        {
            Assert.Equal(household, m.HouseholdId);
            Assert.Equal(Monday, m.WeekStart);
        });
        Assert.Null(generated.ItemId);
        Assert.Equal([item, item, item, item], new[] { added.ItemId, checkedMessage.ItemId, unchecked_.ItemId, removed.ItemId }.Select(i => i!.Value));
    }

    [Fact]
    public async Task A_client_in_another_household_never_receives_the_events()
    {
        await using var host = Host();
        var (userA, householdA, hubA) = await JoinedAsync(host);
        var (userB, householdB, hubB) = await JoinedAsync(host);
        await using var clientA = hubA;
        await using var clientB = hubB;

        var item = await AddManualAsync(userA.Client, householdA, "Only for A");
        await CheckAsync(userA.Client, householdA, item, true);

        // A got both; B had every chance to get them and got nothing.
        Assert.Equal(ShoppingListChangeKind.ManualItemAdded, (await clientA.NextAsync()).Kind);
        Assert.Equal(ShoppingListChangeKind.ItemChecked, (await clientA.NextAsync()).Kind);
        Assert.True(await clientB.StaysSilentAsync(TimeSpan.FromSeconds(1)));

        // And the other way round: B's own change reaches B only.
        await AddManualAsync(userB.Client, householdB, "Only for B");
        var own = await clientB.NextAsync();
        Assert.Equal(householdB, own.HouseholdId);
        Assert.True(await clientA.StaysSilentAsync(TimeSpan.FromSeconds(1)));

        // B cannot ask for A's state either.
        await Assert.ThrowsAsync<HubException>(() => clientB.GetStateAsync(householdA, Monday));
    }

    [Fact]
    public async Task Events_flow_only_between_join_and_leave()
    {
        await using var host = Host();
        var user = await host.SignInAsync();
        var household = await user.Client.CreateHouseholdAsync();
        await using var hub = new HubTestClient(host, user.Client.AuthCookie);
        await hub.StartAsync(); // connected but has not joined

        await AddManualAsync(user.Client, household, "Before joining");

        Assert.True(await hub.StaysSilentAsync(TimeSpan.FromSeconds(1)));

        await hub.JoinAsync(household);
        await AddManualAsync(user.Client, household, "After joining");
        Assert.Equal(ShoppingListChangeKind.ManualItemAdded, (await hub.NextAsync()).Kind);

        await hub.Connection.InvokeAsync("LeaveHousehold", household, TestContext.Current.CancellationToken);
        await AddManualAsync(user.Client, household, "After leaving");
        Assert.True(await hub.StaysSilentAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task After_a_reconnect_rejoining_and_GetState_return_the_current_list_and_a_new_version()
    {
        await using var host = Host();
        var (user, household, hubOwner) = await JoinedAsync(host);
        await using var hub = hubOwner;
        var item = await AddManualAsync(user.Client, household, "Milk");
        await hub.NextAsync();
        var before = await hub.GetStateAsync(household, Monday);
        var beforeAgain = await hub.GetStateAsync(household, Monday);
        Assert.Equal(before.GetProperty("version").GetString(), beforeAgain.GetProperty("version").GetString()); // stable while nothing changes

        await hub.StopAsync();
        await CheckAsync(user.Client, household, item, true); // missed: nobody listens
        await AddManualAsync(user.Client, household, "Bread"); // missed

        await hub.StartAsync();
        Assert.True(await hub.StaysSilentAsync(TimeSpan.FromSeconds(1))); // missed events are not replayed...
        await hub.JoinAsync(household);
        var after = await hub.GetStateAsync(household, Monday); // ...the client resyncs by state

        Assert.NotEqual(before.GetProperty("version").GetString(), after.GetProperty("version").GetString());
        var items = after.GetProperty("list").GetProperty("main").EnumerateArray().ToList();
        Assert.Equal(["Bread", "Milk"], items.Select(i => i.GetProperty("name").GetString()!).Order().ToList());
        Assert.True(items.Single(i => i.GetProperty("name").GetString() == "Milk").GetProperty("checked").GetBoolean());

        // And live events flow again after the rejoin.
        await AddManualAsync(user.Client, household, "Eggs");
        Assert.Equal(ShoppingListChangeKind.ManualItemAdded, (await hub.NextAsync()).Kind);

        // GetState equals what REST returns (same shape).
        using var rest = await user.Client.GetAsync(ListUrl(household));
        var restList = await rest.JsonAsync();
        var final = await hub.GetStateAsync(household, Monday);
        Assert.Equal(restList.GetRawText(), final.GetProperty("list").GetRawText());
    }

    [Fact]
    public async Task GetState_for_a_week_without_a_list_is_the_empty_shape_and_a_bad_week_is_rejected()
    {
        await using var host = Host();
        var (_, household, hubOwner) = await JoinedAsync(host);
        await using var hub = hubOwner;

        var empty = await hub.GetStateAsync(household, Monday);

        Assert.Equal(JsonValueKind.Null, empty.GetProperty("list").GetProperty("id").ValueKind);
        Assert.Empty(empty.GetProperty("list").GetProperty("main").EnumerateArray());
        await Assert.ThrowsAsync<HubException>(() => hub.GetStateAsync(household, Monday.AddDays(1)));
    }

    [Fact]
    public async Task A_revoked_member_is_refused_on_the_next_join_and_state_request()
    {
        await using var host = Host();
        var (user, household, hubOwner) = await JoinedAsync(host);
        await using var hub = hubOwner;
        await hub.GetStateAsync(household, Monday); // allowed while a member

        await HouseholdScenario.ScalarAsync(
            db.ConnectionString,
            "DELETE FROM Memberships WHERE HouseholdId = @h AND UserId = @u; SELECT @@ROWCOUNT",
            ("@h", household), ("@u", user.User.Id));

        await Assert.ThrowsAsync<HubException>(() => hub.GetStateAsync(household, Monday));
        await Assert.ThrowsAsync<HubException>(() => hub.JoinAsync(household));
    }
}
