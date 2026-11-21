using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Profiles;
using Foodplanner.Infrastructure.Export;
using Foodplanner.Infrastructure.Oda;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Oda;

/// <summary>T-053: the session-only Oda cart flow against a fake Oda (recorded fixtures) and a real SQL Server.</summary>
public sealed class OdaEndpointTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AuthHost Host(FakeOda fake, Foodplanner.Api.Tests.Profiles.CapturingLoggerProvider? logs = null, params (string, string)[] settings) =>
        OdaHost.Create(db.ConnectionString, fake, logs, null, settings);

    // ---- Roles and policies ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Oda_roles_owner_and_adult_succeed()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);
        var adult = await host.JoinAsync(owner, household, "Adult");
        adult.Client.Language = "en";

        foreach (var client in new[] { owner.Client, adult.Client })
        {
            var (_, state) = await OdaHost.StartAsync(client, household);
            using var response = await OdaHost.CartAsync(client, household, state);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Oda_roles_child_kid_mode_non_member_and_anonymous_are_refused_and_oda_is_never_called()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var kid = await host.NewScenarioAsync();
        var household = kid.Household;
        var child = await host.JoinAsync(kid.Owner, household, "Child");
        var stranger = await host.SignInAsync();
        await stranger.Client.CreateHouseholdAsync();
        var anonymous = host.CreateClient();

        async Task AssertRefusedAsync(ApiClient client, HttpStatusCode status, string? code)
        {
            foreach (var action in new[] { "start", "cart" })
            {
                using var response = await client.PostAsync(OdaHost.Url(household, action), new { confirmed = true, state = "x", code = "y" });
                Assert.Equal(status, response.StatusCode);
                if (code is not null)
                {
                    await response.AssertProblemAsync(status, code);
                }
            }
        }

        await AssertRefusedAsync(child.Client, HttpStatusCode.Forbidden, "forbidden");
        await AssertRefusedAsync(stranger.Client, HttpStatusCode.Forbidden, "forbidden");
        await AssertRefusedAsync(anonymous, HttpStatusCode.Unauthorized, null);
        await kid.Owner.Client.EnterKidModeAsync(household, kid.Profile1);
        await AssertRefusedAsync(kid.Owner.Client, HttpStatusCode.Forbidden, "kid-mode");

        Assert.Equal(0, fake.RequestCount);
    }

    [Fact]
    public async Task Oda_other_household_forbidden_and_its_list_is_not_read()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (_, householdB) = await OdaHost.ScenarioAsync(host);
        var adultOfA = await host.SignInAsync();
        await adultOfA.Client.CreateHouseholdAsync();

        foreach (var action in new[] { "start", "cart" })
        {
            using var response = await adultOfA.Client.PostAsync(OdaHost.Url(householdB, action), new { confirmed = true, state = "x", code = "y" });
            await response.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        Assert.Equal(0, fake.RequestCount);
    }

    // ---- Start -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Start_requires_confirmation_and_creates_no_flow_state()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        foreach (var body in new object[] { new { }, new { confirmed = false } })
        {
            using var response = await owner.Client.PostAsync(OdaHost.Url(household, "start"), body);
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "oda-confirmation-required");
            Assert.DoesNotContain(response.SetCookies(), c => c.StartsWith("__Host-foodplanner-oda-flow=", StringComparison.Ordinal));
        }

        Assert.Equal(0, fake.RequestCount);
    }

    [Fact]
    public async Task Start_builds_pkce_authorize_url_and_never_calls_oda()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        using var response = await owner.Client.PostAsync(OdaHost.Url(household, "start"), new { confirmed = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var authorize = new Uri((await response.JsonAsync()).GetProperty("authorizeUrl").GetString()!);
        var query = OdaHost.Query(authorize);

        Assert.Equal("https://oda.com/o/authorize/", authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(OdaHost.ClientId, query["client_id"]);
        Assert.Equal(OdaHost.Origin + "/oda/callback", query["redirect_uri"]);
        Assert.Equal("mcp", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.True(Base64UrlLength(query["state"]) >= 32, "state has at least 32 bytes");
        Assert.Equal(0, fake.RequestCount);

        // The challenge is the SHA-256 of the verifier held in the flow state: complete the flow and compare with what the token endpoint receives.
        using var cart = await OdaHost.CartAsync(owner.Client, household, query["state"]);
        Assert.Equal(HttpStatusCode.OK, cart.StatusCode);
        var verifier = OdaHost.Form(fake.TokenRequests.Single().Body)["code_verifier"];
        Assert.Equal(Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), query["code_challenge"]);
        Assert.True(Base64UrlLength(verifier) >= 32);

        var cookie = response.SetCookies().Single(c => c.StartsWith("__Host-foodplanner-oda-flow=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(verifier, cookie, StringComparison.Ordinal); // encrypted
        Assert.DoesNotContain(query["state"], cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_edge_cases()
    {
        var fake = new FakeOda();
        await using var host = Host(fake, null, ("Oda:MaxItemsPerAction", "2"));
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        // No list at all.
        using var empty = await owner.Client.PostAsync(OdaHost.Url(household, "start"), new { confirmed = true });
        await empty.AssertProblemAsync(HttpStatusCode.Conflict, "oda-nothing-to-add");

        // Only checked items.
        var one = await OdaHost.AddManualAsync(owner.Client, household, "Melk");
        await OdaHost.CheckAsync(owner.Client, household, one);
        using var checkedOnly = await owner.Client.PostAsync(OdaHost.Url(household, "start"), new { confirmed = true });
        await checkedOnly.AssertProblemAsync(HttpStatusCode.Conflict, "oda-nothing-to-add");

        // More than the maximum.
        await OdaHost.AddManualAsync(owner.Client, household, "A");
        await OdaHost.AddManualAsync(owner.Client, household, "B");
        await OdaHost.AddManualAsync(owner.Client, household, "C");
        using var many = await owner.Client.PostAsync(OdaHost.Url(household, "start"), new { confirmed = true });
        await many.AssertProblemAsync(HttpStatusCode.Conflict, "oda-too-many-items");

        // Not a Monday.
        using var tuesday = await owner.Client.PostAsync(OdaHost.Url(household, "start", "2026-10-27"), new { confirmed = true });
        Assert.Equal(HttpStatusCode.BadRequest, tuesday.StatusCode);
        Assert.True((await tuesday.JsonAsync()).GetProperty("errors").TryGetProperty("weekStart", out _));

        Assert.Equal(0, fake.RequestCount);
    }

    [Fact]
    public async Task Start_without_client_id_is_not_configured()
    {
        var fake = new FakeOda();
        await using var host = Host(fake, null, ("Oda:ClientId", ""));
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        using var start = await owner.Client.PostAsync(OdaHost.Url(household, "start"), new { confirmed = true });
        await start.AssertProblemAsync(HttpStatusCode.NotFound, "oda-not-configured");
        using var cart = await owner.Client.PostAsync(OdaHost.Url(household, "cart"), new { state = "x", code = "y" });
        await cart.AssertProblemAsync(HttpStatusCode.NotFound, "oda-not-configured");
        Assert.Equal(0, fake.RequestCount);
    }

    [Fact]
    public async Task Start_with_a_plain_http_non_local_origin_is_not_configured()
    {
        var fake = new FakeOda();
        await using var host = Host(fake, null, ("Oda:WebOrigin", "http://app.example.com"));
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        using var start = await owner.Client.PostAsync(OdaHost.Url(household, "start"), new { confirmed = true });
        await start.AssertProblemAsync(HttpStatusCode.NotFound, "oda-not-configured");
        Assert.Equal(0, fake.RequestCount);
    }

    // ---- Flow state --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Token_exchange_is_public_client_pkce()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        await OdaHost.StartAndCompleteAsync(owner.Client, household);

        var request = fake.TokenRequests.Single();
        var form = OdaHost.Form(request.Body);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal(OdaHost.Code, form["code"]);
        Assert.Equal(OdaHost.Origin + "/oda/callback", form["redirect_uri"]);
        Assert.Equal(OdaHost.ClientId, form["client_id"]);
        Assert.True(form["code_verifier"].Length >= 43);
        Assert.DoesNotContain("client_secret", form.Keys);
        Assert.Null(request.Authorization);
    }

    [Fact]
    public async Task State_invalid_cases_look_the_same_and_oda_is_not_called()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);
        using var otherHousehold = await owner.Client.PostAsync("/api/households", new { name = "Second" });
        var second = (await otherHousehold.JsonAsync()).GetProperty("id").GetGuid();
        var bodies = new List<string>();

        async Task Expect(Task<HttpResponseMessage> call)
        {
            using var response = await call;
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "oda-state-invalid");
            bodies.Add(await response.ProblemShapeAsync());
        }

        // Unknown (no flow started), then a wrong state value.
        await Expect(OdaHost.CartAsync(owner.Client, household, "unknown-state"));
        var (_, state) = await OdaHost.StartAsync(owner.Client, household);
        await Expect(OdaHost.CartAsync(owner.Client, household, state + "x"));

        // Another household or another week with a real state: refused, and the flow is consumed.
        (_, state) = await OdaHost.StartAsync(owner.Client, household);
        await Expect(OdaHost.CartAsync(owner.Client, second, state));
        (_, state) = await OdaHost.StartAsync(owner.Client, household);
        await Expect(OdaHost.CartAsync(owner.Client, household, state, week: "2026-11-02"));

        // Expired: the fake clock moves past 10 minutes.
        (_, state) = await OdaHost.StartAsync(owner.Client, household);
        host.Time.Now = host.Time.Now.AddMinutes(11);
        await Expect(OdaHost.CartAsync(owner.Client, household, state));
        host.Time.Now = host.Time.Now.AddMinutes(-11);

        // Reused: the first call works, the second is refused.
        (_, state) = await OdaHost.StartAsync(owner.Client, household);
        using (var first = await OdaHost.CartAsync(owner.Client, household, state))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        var requestsBeforeReplay = fake.RequestCount;
        await Expect(OdaHost.CartAsync(owner.Client, household, state));
        Assert.Equal(requestsBeforeReplay, fake.RequestCount);

        Assert.Single(bodies.Distinct());
        Assert.Single(fake.TokenRequests); // only the one valid completion reached Oda
    }

    [Fact]
    public async Task State_survives_second_replica()
    {
        // Two hosts as two replicas: separate memory (no shared flow state), one shared Data Protection key ring (T-016 in a deployment).
        var keys = Directory.CreateTempSubdirectory("oda-dp-");
        try
        {
            void ShareKeys(IServiceCollection services) =>
                services.AddDataProtection().SetApplicationName("foodplanner-t053-test").PersistKeysToFileSystem(keys);
            var fake = new FakeOda();
            await using var replica1 = OdaHost.Create(db.ConnectionString, fake, null, ShareKeys);
            await using var replica2 = OdaHost.Create(db.ConnectionString, fake, null, ShareKeys);
            var (owner, household) = await OdaHost.ScenarioAsync(replica1);
            var second = replica2.CreateClient();
            using var login = await second.LoginAsync(owner.User.Email, AuthHost.Password);
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

            using var start = await owner.Client.PostAsync(OdaHost.Url(household, "start"), new { confirmed = true });
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            var state = OdaHost.Query(new Uri((await start.JsonAsync()).GetProperty("authorizeUrl").GetString()!))["state"];
            var cookie = start.SetCookies().Single(c => c.StartsWith("__Host-foodplanner-oda-flow=", StringComparison.Ordinal)).Split(';')[0];

            // The browser carries the cookie to whichever replica serves the second call.
            using var cart = await second.PostAsync(OdaHost.Url(household, "cart"), new { state, code = OdaHost.Code }, new Dictionary<string, string> { ["Cookie"] = cookie });

            Assert.Equal(HttpStatusCode.OK, cart.StatusCode);
            Assert.Equal("done", (await cart.JsonAsync()).GetProperty("status").GetString());
        }
        finally
        {
            keys.Delete(true);
        }
    }

    [Fact]
    public async Task Cancelled_at_oda_returns_cancelled_without_token_or_revoke_and_cannot_be_reused()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);
        var (_, state) = await OdaHost.StartAsync(owner.Client, household);

        using var cancelled = await owner.Client.PostAsync(OdaHost.Url(household, "cart"), new { state, error = "access_denied" });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("cancelled", (await cancelled.JsonAsync()).GetProperty("status").GetString());
        Assert.Equal(0, fake.RequestCount);

        using var reuse = await OdaHost.CartAsync(owner.Client, household, state);
        await reuse.AssertProblemAsync(HttpStatusCode.BadRequest, "oda-state-invalid");
        Assert.Equal(0, fake.RequestCount);
    }

    // ---- Cart --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cart_sends_only_unchecked_main_items()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host); // Rice, Egg (main), Salt (check you have)
        var checkedItem = await OdaHost.AddManualAsync(owner.Client, household, "Checked thing");
        await OdaHost.AddManualAsync(owner.Client, household, "Kaffefilter");
        await OdaHost.CheckAsync(owner.Client, household, checkedItem);

        await OdaHost.StartAndCompleteAsync(owner.Client, household);

        var queries = fake.ToolCalls("product_search").SelectMany(c => c["queries"]!.AsArray().Select(q => q!.GetValue<string>())).Order().ToArray();
        Assert.Equal(["Egg", "Kaffefilter", "Rice"], queries);
    }

    [Fact]
    public async Task Search_is_batched_and_minimal()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        owner.Client.Language = "en";
        for (var i = 1; i <= 25; i++)
        {
            await OdaHost.AddManualAsync(owner.Client, household, $"Item {i}");
        }

        var result = await OdaHost.StartAndCompleteAsync(owner.Client, household);

        var searches = fake.ToolCalls("product_search");
        Assert.Equal([10, 10, 5], searches.Select(s => s["queries"]!.AsArray().Count).ToArray());
        Assert.All(searches, s => Assert.InRange(s["size"]!.GetValue<int>(), 1, 5));
        Assert.Equal(3, fake.ToolCalls("manipulate_cart").Count);
        Assert.Single(fake.ToolCalls("get_cart"));
        Assert.All(fake.AllToolNames, n => Assert.Contains(n, new[] { "product_search", "manipulate_cart", "get_cart" }));
        Assert.Equal(25, result.GetProperty("added").GetArrayLength());
        Assert.Equal("done", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Matching_rules_status_cart_url_and_messages()
    {
        var fake = new FakeOda();
        fake.RefuseProducts.Add(1006);
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host); // Rice (available), Egg (first hit sold out)
        await OdaHost.AddManualAsync(owner.Client, household, "Kaffefilter"); // no hits
        await OdaHost.AddManualAsync(owner.Client, household, "Melk"); // hits, none available
        await OdaHost.AddManualAsync(owner.Client, household, "Refused"); // the cart refuses the line

        var result = await OdaHost.StartAndCompleteAsync(owner.Client, household);

        Assert.Equal("partial", result.GetProperty("status").GetString());
        var added = result.GetProperty("added").EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!, a => a);
        Assert.Equal(["Egg", "Rice"], added.Keys.Order().ToArray());
        Assert.Equal("Fake Egg 12 stk", added["Egg"].GetProperty("productName").GetString()); // the sold-out first hit was skipped
        Assert.Equal("Fake Basmatiris 1 kg", added["Rice"].GetProperty("productName").GetString());
        Assert.All(added.Values, a => Assert.Equal(1, a.GetProperty("quantity").GetInt32()));
        var notAdded = result.GetProperty("notAdded").EnumerateArray().ToDictionary(n => n.GetProperty("name").GetString()!, n => n.GetProperty("reason").GetString());
        Assert.Equal("no-match", notAdded["Kaffefilter"]);
        Assert.Equal("unavailable", notAdded["Melk"]);
        Assert.Equal("rejected", notAdded["Refused"]);
        Assert.Equal("https://oda.com/no/cart/", result.GetProperty("cartUrl").GetString());
        Assert.Contains("sold out", result.GetProperty("cartMessages")[0].GetString(), StringComparison.Ordinal);
        Assert.True(result.GetProperty("connectionClosed").GetBoolean());

        // Every cart call added one pack per matched item, never more and never a removal.
        var operations = fake.ToolCalls("manipulate_cart").SelectMany(c => c["operations"]!.AsArray()).ToList();
        Assert.All(operations, o => Assert.Equal(1, o!["quantity"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Done_and_nothing_added_statuses()
    {
        var all = new FakeOda();
        await using (var host = Host(all))
        {
            var owner = await host.SignInAsync();
            var household = await owner.Client.CreateHouseholdAsync();
            owner.Client.Language = "en";
            await OdaHost.AddManualAsync(owner.Client, household, "Rice");
            Assert.Equal("done", (await OdaHost.StartAndCompleteAsync(owner.Client, household)).GetProperty("status").GetString());
        }

        var none = new FakeOda();
        await using (var host = Host(none))
        {
            var owner = await host.SignInAsync();
            var household = await owner.Client.CreateHouseholdAsync();
            owner.Client.Language = "en";
            await OdaHost.AddManualAsync(owner.Client, household, "Kaffefilter");
            Assert.Equal("nothing-added", (await OdaHost.StartAndCompleteAsync(owner.Client, household)).GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Sse_replies_are_understood()
    {
        var fake = new FakeOda { Sse = true };
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        var result = await OdaHost.StartAndCompleteAsync(owner.Client, household);

        Assert.Equal("done", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unknown_outcome_is_not_repeated()
    {
        var fake = new FakeOda { TimeoutTool = "manipulate_cart" };
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);
        var (_, state) = await OdaHost.StartAsync(owner.Client, household);

        using var response = await OdaHost.CartAsync(owner.Client, household, state);

        await response.AssertProblemAsync(HttpStatusCode.BadGateway, "oda-unavailable");
        Assert.Single(fake.ToolCalls("manipulate_cart"));
        Assert.NotEmpty(fake.RevokeRequests);
    }

    // ---- Revoking ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Token_revoked_after_success()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        var result = await OdaHost.StartAndCompleteAsync(owner.Client, household);

        var revoked = fake.RevokeRequests.Select(r => OdaHost.Form(r.Body)).ToList();
        Assert.Equal(2, revoked.Count); // the access token and the refresh token
        Assert.Contains(revoked, f => f["token"] == fake.AccessToken && f["token_type_hint"] == "access_token");
        Assert.Contains(revoked, f => f["token"] == fake.RefreshToken && f["token_type_hint"] == "refresh_token");
        Assert.All(revoked, f => Assert.Equal(OdaHost.ClientId, f["client_id"]));
        Assert.True(result.GetProperty("connectionClosed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("connectionMessage").ValueKind);

        Assert.Single(fake.TokenRequests);
    }

    [Fact]
    public async Task Only_the_access_token_is_revoked_when_no_refresh_token_was_issued()
    {
        var fake = new FakeOda { IssueRefreshToken = false };
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        await OdaHost.StartAndCompleteAsync(owner.Client, household);

        var revoke = Assert.Single(fake.RevokeRequests);
        Assert.Equal(fake.AccessToken, OdaHost.Form(revoke.Body)["token"]);
    }

    [Theory]
    [InlineData("product_search")]
    [InlineData("manipulate_cart")]
    [InlineData("get_cart")]
    public async Task Token_revoked_on_error(string failingTool)
    {
        var fake = new FakeOda { FailTool = failingTool };
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);
        var (_, state) = await OdaHost.StartAsync(owner.Client, household);

        using var response = await OdaHost.CartAsync(owner.Client, household, state);

        Assert.NotEmpty(fake.RevokeRequests);
        Assert.Contains(fake.RevokeRequests, r => OdaHost.Form(r.Body)["token"] == fake.AccessToken);
        if (failingTool != "get_cart")
        {
            // A cart link that could not be read is not a failure: the items are in the cart.
            await response.AssertProblemAsync(HttpStatusCode.BadGateway, "oda-unavailable");
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(JsonValueKind.Null, (await response.JsonAsync()).GetProperty("cartUrl").ValueKind);
        }
    }

    [Fact]
    public async Task Revoke_failure_is_reported_after_one_retry()
    {
        var fake = new FakeOda { IssueRefreshToken = false, RevokeFailures = 2 };
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        var result = await OdaHost.StartAndCompleteAsync(owner.Client, household);

        Assert.Equal(2, fake.RevokeRequests.Count);
        Assert.False(result.GetProperty("connectionClosed").GetBoolean());
        Assert.Contains("Privacy & security", result.GetProperty("connectionMessage").GetString(), StringComparison.Ordinal);
        Assert.Equal("done", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_revoke_that_never_answers_times_out_twice_and_reports_the_connection_open()
    {
        var fake = new FakeOda { IssueRefreshToken = false, RevokeHangs = true };
        await using var host = Host(fake, null, ("Oda:RevokeTimeoutSeconds", "1"));
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        var result = await OdaHost.StartAndCompleteAsync(owner.Client, household);

        Assert.Equal(2, fake.RevokeRequests.Count);
        Assert.False(result.GetProperty("connectionClosed").GetBoolean());
    }

    [Fact]
    public async Task A_single_revoke_failure_is_retried_once_and_closes_the_connection()
    {
        var fake = new FakeOda { IssueRefreshToken = false, RevokeFailures = 1 };
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        var result = await OdaHost.StartAndCompleteAsync(owner.Client, household);

        Assert.Equal(2, fake.RevokeRequests.Count);
        Assert.True(result.GetProperty("connectionClosed").GetBoolean());
    }

    // ---- Nothing kept, nothing leaked --------------------------------------------------------------------------------

    [Fact]
    public async Task Tokens_never_persisted_and_no_product_data_kept()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);
        await OdaHost.StartAndCompleteAsync(owner.Client, household);
        var verifier = OdaHost.Form(fake.TokenRequests.Single().Body)["code_verifier"];

        var hits = await OdaHost.FindInDatabaseAsync(
            db.ConnectionString, fake.AccessToken, fake.RefreshToken, OdaHost.Code, verifier, "Fake Egg 12", "Fake Basmatiris", "oda.com/no/cart");

        Assert.Empty(hits);
        using var scope = host.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<Foodplanner.Infrastructure.Persistence.AppDbContext>().Model;
        Assert.DoesNotContain(model.GetEntityTypes(), e => e.ClrType.Name.Contains("Oda", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Tokens_never_logged()
    {
        var fake = new FakeOda();
        var logs = new Foodplanner.Api.Tests.Profiles.CapturingLoggerProvider();
        await using var host = Host(fake, logs);
        var (owner, household) = await OdaHost.ScenarioAsync(host);
        var (_, state) = await OdaHost.StartAsync(owner.Client, household);
        using var ok = await OdaHost.CartAsync(owner.Client, household, state);
        var verifier = OdaHost.Form(fake.TokenRequests.Single().Body)["code_verifier"];

        var failing = new FakeOda { FailTool = "manipulate_cart" };
        await using var failHost = Host(failing, logs);
        var (failOwner, failHousehold) = await OdaHost.ScenarioAsync(failHost);
        var (_, failState) = await OdaHost.StartAsync(failOwner.Client, failHousehold);
        using var failed = await OdaHost.CartAsync(failOwner.Client, failHousehold, failState);
        var failBody = await failed.Content.ReadAsStringAsync(Ct);

        string[] secrets = [fake.AccessToken, fake.RefreshToken, OdaHost.Code, verifier, state, failState, "upstream-error-body-should-not-leak"];
        string[] messages;
        lock (logs.Messages)
        {
            messages = [.. logs.Messages];
        }

        Assert.Contains(messages, m => m.Contains("Oda cart action", StringComparison.Ordinal)); // not vacuous: the one line is there
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(messages, m => m.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(secret, failBody, StringComparison.Ordinal);
        }

        Assert.Equal("[redacted]", new OdaSecret("topsecret").ToString());
        Assert.Equal("[redacted]", new OdaTokens(new OdaSecret("topsecret"), null).ToString());
        Assert.DoesNotContain("topsecret", $"{new OdaTokens(new OdaSecret("topsecret"), new OdaSecret("topsecret2"))}", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_not_sent_to_browser()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        using var start = await owner.Client.PostAsync(OdaHost.Url(household, "start"), new { confirmed = true });
        var state = OdaHost.Query(new Uri((await start.JsonAsync()).GetProperty("authorizeUrl").GetString()!))["state"];
        using var cart = await OdaHost.CartAsync(owner.Client, household, state);

        foreach (var response in new[] { start, cart })
        {
            var text = response.Headers.ToString() + response.Content.Headers + await response.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain(fake.AccessToken, text, StringComparison.Ordinal);
            Assert.DoesNotContain(fake.RefreshToken, text, StringComparison.Ordinal);
            Assert.DoesNotContain(OdaHost.Code, text, StringComparison.Ordinal);
        }
    }

    // ---- Failures ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("search-500")]
    [InlineData("rate-limited-429")]
    [InlineData("code-refused")]
    [InlineData("tool-missing")]
    [InlineData("argument-missing")]
    [InlineData("field-missing")]
    [InlineData("cart-500-after-search")]
    public async Task Oda_failures_degrade_to_the_text_export(string failure)
    {
        var fake = new FakeOda();
        switch (failure)
        {
            case "search-500":
                fake.FailTool = "product_search";
                break;
            case "rate-limited-429":
                fake.McpStatus = HttpStatusCode.TooManyRequests;
                break;
            case "code-refused":
                fake.TokenStatus = HttpStatusCode.BadRequest;
                break;
            case "tool-missing":
                fake.OmitTools.Add("manipulate_cart");
                break;
            case "argument-missing":
                fake.OmitArguments.Add("product_search.queries");
                break;
            case "field-missing":
                fake.OmitCartFields.Add("groups");
                break;
            default:
                fake.FailTool = "manipulate_cart";
                break;
        }

        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);
        var exportUrl = $"/api/households/{household}/shopping-lists/{OdaHost.Monday}/export?format=text";
        using var before = await owner.Client.GetAsync(exportUrl);
        var textBefore = (await before.JsonAsync()).GetProperty("text").GetString();
        var (_, state) = await OdaHost.StartAsync(owner.Client, household);

        using var response = await OdaHost.CartAsync(owner.Client, household, state);

        await response.AssertProblemAsync(HttpStatusCode.BadGateway, "oda-unavailable");
        var body = await response.JsonAsync();
        Assert.Equal("text", body.GetProperty("fallback").GetProperty("export").GetString());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("added").ValueKind);
        Assert.True(body.GetProperty("connectionClosed").GetBoolean());
        Assert.Equal(fake.TokenRequests.Count > 0 && failure != "code-refused", fake.RevokeRequests.Count > 0);
        var raw = body.GetRawText();
        Assert.DoesNotContain("upstream", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invalid_grant", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(fake.AccessToken, raw, StringComparison.Ordinal);

        using var after = await owner.Client.GetAsync(exportUrl);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(textBefore, (await after.JsonAsync()).GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_failure_after_some_batches_reports_what_was_already_added()
    {
        var fake = new FakeOda();
        var calls = 0;
        fake.BeforeTool = name =>
        {
            if (name == "manipulate_cart" && ++calls == 2)
            {
                fake.FailTool = "manipulate_cart";
            }
        };
        await using var host = Host(fake);
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        owner.Client.Language = "en";
        for (var i = 1; i <= 15; i++)
        {
            await OdaHost.AddManualAsync(owner.Client, household, $"Item {i}");
        }

        var (_, state) = await OdaHost.StartAsync(owner.Client, household);
        using var response = await OdaHost.CartAsync(owner.Client, household, state);

        await response.AssertProblemAsync(HttpStatusCode.BadGateway, "oda-unavailable");
        Assert.Equal(10, (await response.JsonAsync()).GetProperty("added").GetArrayLength());
        Assert.Equal(2, fake.ToolCalls("manipulate_cart").Count); // the failed call was not repeated
    }

    [Fact]
    public async Task Oda_hosts_are_allow_listed()
    {
        var fake = new FakeOda { TokenHost = "evil.example" };
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);
        var (_, state) = await OdaHost.StartAsync(owner.Client, household);

        using var response = await OdaHost.CartAsync(owner.Client, household, state);

        await response.AssertProblemAsync(HttpStatusCode.BadGateway, "oda-unavailable");
        Assert.DoesNotContain(fake.Requests, r => r.Url.Host == "evil.example");
        Assert.Empty(fake.TokenRequests);
        Assert.Empty(fake.RevokeRequests); // no token existed
    }

    [Fact]
    public async Task Nothing_is_ordered_only_search_add_and_cart_are_called()
    {
        var fake = new FakeOda(); // throws on any other tool, and on a negative quantity
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        await OdaHost.StartAndCompleteAsync(owner.Client, household);

        Assert.All(fake.AllToolNames, n => Assert.Contains(n, new[] { "product_search", "manipulate_cart", "get_cart" }));
        Assert.All(fake.Requests, r => Assert.True(r.Url.AbsolutePath is "/mcp" or "/o/token/" or "/o/revoke_token/" || r.Url.AbsolutePath.StartsWith("/.well-known/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Rate_limited()
    {
        var fake = new FakeOda();
        await using var host = Host(fake, null, ("RateLimiting:Oda:PermitLimit", "2"));
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        for (var i = 0; i < 2; i++)
        {
            using var ok = await owner.Client.PostAsync(OdaHost.Url(household, "start"), new { confirmed = true });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await owner.Client.PostAsync(OdaHost.Url(household, "start"), new { confirmed = true });
        await limited.AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate-limited");
        Assert.True(limited.Headers.Contains("Retry-After"));
    }

    // ---- Exporter ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_oda_exporter_is_selectable_summarises_and_never_calls_oda()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);
        using var scope = host.Services.CreateScope();

        var exporter = scope.ServiceProvider.GetRequiredService<IShoppingListExporterSelector>().Find("oda");
        var text = scope.ServiceProvider.GetRequiredService<IShoppingListExporterSelector>().Find("text");

        Assert.NotNull(exporter);
        Assert.IsType<OdaShoppingListExporter>(exporter);
        Assert.NotNull(text);
        using var response = await owner.Client.GetAsync($"/api/households/{household}/shopping-lists/{OdaHost.Monday}/export?format=oda");
        var body = await response.JsonAsync();
        Assert.Equal("oda", body.GetProperty("format").GetString());
        Assert.Contains("- Rice", body.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Salt", body.GetProperty("text").GetString(), StringComparison.Ordinal); // "check you have" is not sent
        Assert.Equal(0, fake.RequestCount);
    }

    private static int Base64UrlLength(string value) =>
        Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '=')).Length;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
