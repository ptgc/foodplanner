using System.Net;
using System.Text.Json;
using Foodplanner.Api.Authorization;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.Profiles;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.ShoppingLists.Sync;
using Foodplanner.Infrastructure.Oda;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Oda;

/// <summary>T-130: "Sync with Oda" (start, review) and the bulk save of Oda choices, against a fake Oda (recorded fixtures) and a real SQL Server.</summary>
public sealed class OdaSyncTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly Guid Rice = Seed("ris");
    private static readonly Guid Egg = Seed("egg");
    private static readonly Guid Pasta = Seed("pasta");
    private static readonly Guid Bacon = Seed("bacon");
    private static readonly Guid Salt = Seed("salt");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid Seed(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    private AuthHost Host(FakeOda fake, Foodplanner.Api.Tests.Profiles.CapturingLoggerProvider? logs = null, params (string, string)[] settings) =>
        OdaHost.Create(db.ConnectionString, fake, logs, null, settings);

    private static string Url(Guid household, string action) => $"/api/households/{household}/oda-sync/{action}";

    private static string PrefUrl(Guid household) => $"/api/households/{household}/oda-preferences";

    private static async Task<(JsonElement Body, string State)> StartAsync(ApiClient client, Guid household, object? body = null)
    {
        using var response = await client.PostAsync(Url(household, "start"), body ?? new { confirmed = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.JsonAsync();
        return (json, OdaHost.Query(new Uri(json.GetProperty("authorizeUrl").GetString()!))["state"]);
    }

    private static Task<HttpResponseMessage> ReviewAsync(ApiClient client, Guid household, string state) =>
        client.PostAsync(Url(household, "review"), new { state, code = OdaHost.Code });

    private static async Task<JsonElement> RunAsync(ApiClient client, Guid household, object? body = null)
    {
        var (_, state) = await StartAsync(client, household, body);
        using var response = await ReviewAsync(client, household, state);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static Task<HttpResponseMessage> BulkAsync(ApiClient client, Guid household, params (Guid Ingredient, long Product, bool Replace)[] items) =>
        client.PutAsync(PrefUrl(household), new { items = items.Select(i => new { ingredientId = i.Ingredient, odaProductId = i.Product, replace = i.Replace }).ToArray() });

    private async Task<long> RowsAsync(Guid household) =>
        Convert.ToInt64(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM PreferredOdaProducts WHERE HouseholdId = @h", ("@h", household)));

    private async Task<long?> StoredAsync(Guid household, Guid ingredient) =>
        await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT OdaProductId FROM PreferredOdaProducts WHERE HouseholdId = @h AND IngredientId = @i", ("@h", household), ("@i", ingredient)) as long?;

    /// <summary>Own ingredients named "{prefix} 001", ... in recipes of at most 100 lines each (the recipe limit). Returns the ids.</summary>
    private static async Task<List<Guid>> AddIngredientsAsync(ApiClient client, Guid household, string prefix, int count, string[]? names = null)
    {
        var gram = UnitSeed.All.Single(r => r.Code == "g").Id;
        var ids = new List<Guid>();
        for (var i = 1; i <= count; i++)
        {
            using var created = await client.PostAsync(
                $"/api/households/{household}/ingredients", new { name = names?[i - 1] ?? $"{prefix} {i:000}", isStaple = false, dimension = "Mass" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            ids.Add((await created.JsonAsync()).GetProperty("id").GetGuid());
        }

        foreach (var chunk in ids.Chunk(100))
        {
            using var recipe = await client.PostAsync($"/api/households/{household}/recipes", new
            {
                title = $"{prefix} recipe {Guid.NewGuid():N}",
                servings = 4,
                ingredients = chunk.Select(id => new { ingredientId = id, quantity = (decimal?)1, unitId = (Guid?)gram, note = (string?)null }).ToArray(),
            });
            Assert.Equal(HttpStatusCode.Created, recipe.StatusCode);
        }

        return ids;
    }

    /// <summary>A recipe made of global ingredients (their names are the request language's).</summary>
    private static async Task AddGlobalAsync(ApiClient client, Guid household, params Guid[] ingredients)
    {
        var gram = UnitSeed.All.Single(r => r.Code == "g").Id;
        using var recipe = await client.PostAsync($"/api/households/{household}/recipes", new
        {
            title = "Global " + Guid.NewGuid().ToString("N")[..6],
            servings = 4,
            ingredients = ingredients.Select(id => new { ingredientId = id, quantity = (decimal?)500, unitId = (Guid?)gram, note = (string?)null }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Created, recipe.StatusCode);
    }

    private static async Task<(SignedInUser Owner, Guid Household)> EmptyAsync(AuthHost host)
    {
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        owner.Client.Language = "en";
        return (owner, household);
    }

    private static string[] Names(JsonElement review, string category) =>
        [.. review.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("category").GetString() == category).Select(r => r.GetProperty("ingredientName").GetString()!)];

    private static JsonElement Row(JsonElement review, string name) =>
        review.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("ingredientName").GetString() == name);

    // ---- Roles, tenancy, policies ------------------------------------------------------------------------------------

    [Fact]
    public async Task Sync_roles_owner_and_adult_succeed()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);
        var adult = await host.JoinAsync(owner, household, "Adult");
        adult.Client.Language = "en";

        foreach (var client in new[] { owner.Client, adult.Client })
        {
            var review = await RunAsync(client, household);
            Assert.Equal("done", review.GetProperty("status").GetString());
            using var save = await BulkAsync(client, household, (Rice, 1001, true));
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        }
    }

    [Fact]
    public async Task Sync_roles_child_kid_mode_non_member_and_anonymous_are_refused_without_a_call_to_oda_or_a_write()
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
            using var start = await client.PostAsync(Url(household, "start"), new { confirmed = true });
            using var review = await client.PostAsync(Url(household, "review"), new { state = "x", code = "y" });
            using var bulk = await BulkAsync(client, household, (Rice, 1001, false));
            foreach (var response in new[] { start, review, bulk })
            {
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
        Assert.Equal(0, await RowsAsync(household));
    }

    [Fact]
    public async Task Sync_other_household_forbidden_and_its_data_is_not_read()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (_, householdB) = await OdaHost.ScenarioAsync(host);
        var adultOfA = await host.SignInAsync();
        await adultOfA.Client.CreateHouseholdAsync();

        foreach (var action in new[] { "start", "review" })
        {
            using var response = await adultOfA.Client.PostAsync(Url(householdB, action), new { confirmed = true, state = "x", code = "y" });
            await response.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using var bulk = await BulkAsync(adultOfA.Client, householdB, (Rice, 1001, false));
        await bulk.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(0, fake.RequestCount);
        Assert.Equal(0, await RowsAsync(householdB));
    }

    [Fact]
    public async Task Sync_endpoints_policies()
    {
        await using var host = AuthHost.Create(connectionString: string.Empty);
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.Contains("/oda-sync/", StringComparison.Ordinal)
                        || (e.RoutePattern.RawText.TrimEnd((char)47).EndsWith("/oda-preferences", StringComparison.Ordinal) && e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("PUT")))
            .ToList();

        Assert.Equal(
            [
                "/api/households/{householdId:guid}/oda-preferences",
                "/api/households/{householdId:guid}/oda-sync/review",
                "/api/households/{householdId:guid}/oda-sync/start",
            ],
            endpoints.Select(e => e.RoutePattern.RawText!.TrimEnd((char)47)).Order(StringComparer.Ordinal).ToArray());
        foreach (var endpoint in endpoints)
        {
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
            Assert.Contains(Policies.HouseholdAdult, policies);
            Assert.Contains(Policies.FullSession, policies);
            Assert.NotNull(endpoint.Metadata.GetMetadata<Foodplanner.Api.Security.AntiforgeryValidatedMetadata>());
            var limited = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == Foodplanner.Api.Security.RateLimitPolicies.OdaAction;
            Assert.Equal(!endpoint.RoutePattern.RawText!.TrimEnd((char)47).EndsWith("/oda-preferences", StringComparison.Ordinal), limited);
        }
    }

    // ---- Start -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sync_start_needs_confirmation_builds_the_pkce_url_counts_and_never_calls_oda()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        foreach (var body in new object[] { new { }, new { confirmed = false } })
        {
            using var refused = await owner.Client.PostAsync(Url(household, "start"), body);
            await refused.AssertProblemAsync(HttpStatusCode.BadRequest, "oda-confirmation-required");
            Assert.DoesNotContain(refused.SetCookies(), c => c.StartsWith("__Host-foodplanner-oda-flow=", StringComparison.Ordinal));
        }

        using var negative = await owner.Client.PostAsync(Url(household, "start"), new { confirmed = true, offset = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.True((await negative.JsonAsync()).GetProperty("errors").TryGetProperty("offset", out _));

        var (body2, _) = await StartAsync(owner.Client, household);
        var authorize = new Uri(body2.GetProperty("authorizeUrl").GetString()!);
        var query = OdaHost.Query(authorize);
        Assert.Equal("https://oda.com/o/authorize/", authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal(OdaHost.ClientId, query["client_id"]);
        Assert.Equal(OdaHost.Origin + "/oda/callback", query["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(2, body2.GetProperty("ingredientCount").GetInt32()); // Rice and Egg; Salt is a staple
        Assert.Equal(0, body2.GetProperty("notCheckedCount").GetInt32());
        Assert.Equal(0, fake.RequestCount);
    }

    [Fact]
    public async Task Sync_start_without_a_client_id_is_not_configured()
    {
        var fake = new FakeOda();
        await using var host = Host(fake, null, ("Oda:ClientId", ""));
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        using var response = await owner.Client.PostAsync(Url(household, "start"), new { confirmed = true });

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "oda-not-configured");
        Assert.Equal(0, fake.RequestCount);
    }

    // ---- Which ingredients -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sync_ingredient_selection_staples_existing_choices_other_households_and_alternatives()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host); // Rice, Egg, Salt (a staple)
        var (otherOwner, other) = await OdaHost.ScenarioAsync(host);
        await AddIngredientsAsync(otherOwner.Client, other, "Foreign", 3);
        owner.Client.Language = "en";

        async Task<int> CountAsync(object body)
        {
            using var response = await owner.Client.PostAsync(Url(household, "start"), body);
            return response.StatusCode == HttpStatusCode.OK ? (await response.JsonAsync()).GetProperty("ingredientCount").GetInt32() : 0;
        }

        Assert.Equal(2, await CountAsync(new { confirmed = true }));
        Assert.Equal(3, await CountAsync(new { confirmed = true, includeStaples = true }));
        using (var set = await owner.Client.PutAsync(PrefUrl(household) + $"/{Rice}", new { odaProductId = 1001 }))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }

        Assert.Equal(1, await CountAsync(new { confirmed = true }));
        Assert.Equal(2, await CountAsync(new { confirmed = true, includeExisting = true }));
        Assert.Equal(3, await CountAsync(new { confirmed = true, includeExisting = true, includeStaples = true }));

        // The primary ingredient of a line only: Butter with an alternative (Oil) selects Butter and not the alternative.
        var (alt, altHousehold) = await EmptyAsync(host);
        using var recipe = await alt.Client.PostAsync($"/api/households/{altHousehold}/recipes", new
        {
            title = "Fried " + Guid.NewGuid().ToString("N")[..6],
            servings = 4,
            ingredients = new object[]
            {
                new
                {
                    ingredientId = Seed("smor"), quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null, amountKind = "AsNeeded",
                    purpose = "Frying", isOptional = false, alternativeIds = new[] { Seed("rapsolje") },
                },
            },
        });
        Assert.Equal(HttpStatusCode.Created, recipe.StatusCode);
        var (_, altState) = await StartAsync(alt.Client, altHousehold, new { confirmed = true, includeStaples = true });
        using var altReview = await ReviewAsync(alt.Client, altHousehold, altState);
        var rows = (await altReview.JsonAsync()).GetProperty("rows").EnumerateArray().ToList();
        Assert.Single(rows);
        Assert.Equal(Seed("smor"), rows[0].GetProperty("ingredientId").GetGuid());
        Assert.DoesNotContain(fake.ToolCalls("product_search").SelectMany(c => c["queries"]!.AsArray()), q => q!.GetValue<string>().Contains("Foreign", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sync_selection_with_nothing_to_sync_is_a_conflict_and_no_cookie_is_issued()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await EmptyAsync(host);

        using var none = await owner.Client.PostAsync(Url(household, "start"), new { confirmed = true });

        await none.AssertProblemAsync(HttpStatusCode.Conflict, "oda-sync-nothing-to-sync");
        Assert.DoesNotContain(none.SetCookies(), c => c.StartsWith("__Host-foodplanner-oda-flow=", StringComparison.Ordinal));
        Assert.Equal(0, fake.RequestCount);
    }

    [Fact]
    public async Task Sync_offset_and_cap_page_the_list_without_overlap_or_gaps()
    {
        var fake = new FakeOda();
        await using var host = Host(fake, null, ("Oda:MaxSyncIngredients", "10"));
        var (owner, household) = await EmptyAsync(host);
        await AddIngredientsAsync(owner.Client, household, "Paged", 25);

        var seen = new List<string>();
        foreach (var (offset, count, notChecked) in new[] { (0, 10, 15), (10, 10, 5), (20, 5, 0) })
        {
            var (start, _) = await StartAsync(owner.Client, household, new { confirmed = true, offset });
            Assert.Equal(count, start.GetProperty("ingredientCount").GetInt32());
            Assert.Equal(notChecked, start.GetProperty("notCheckedCount").GetInt32());
            var review = await RunAsync(owner.Client, household, new { confirmed = true, offset });
            Assert.Equal(notChecked, review.GetProperty("notCheckedCount").GetInt32());
            seen.AddRange(review.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("ingredientName").GetString()!));
        }

        Assert.Equal(Enumerable.Range(1, 25).Select(i => $"Paged {i:000}").ToArray(), seen.Order(StringComparer.Ordinal).ToArray());
        using var beyond = await owner.Client.PostAsync(Url(household, "start"), new { confirmed = true, offset = 25 });
        await beyond.AssertProblemAsync(HttpStatusCode.Conflict, "oda-sync-nothing-to-sync");
    }

    // ---- Call budget and classification ------------------------------------------------------------------------------

    [Theory]
    [InlineData(150, 15)]
    [InlineData(25, 3)]
    public async Task Sync_call_budget_is_batches_of_ten_in_sequence_and_one_history_call_last(int ingredients, int searches)
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await EmptyAsync(host);
        await AddIngredientsAsync(owner.Client, household, "Budget", ingredients);

        var review = await RunAsync(owner.Client, household);

        var calls = fake.AllToolNames;
        Assert.Equal(searches + 1, calls.Count);
        Assert.Equal(Enumerable.Repeat("product_search", searches).Append("likely_to_buy"), calls);
        var arguments = fake.ToolCalls("product_search");
        Assert.All(arguments, a => Assert.InRange(a["queries"]!.AsArray().Count, 1, 10));
        Assert.All(arguments, a => Assert.Equal(10, a["size"]!.GetValue<int>()));
        Assert.Equal(ingredients, arguments.Sum(a => a["queries"]!.AsArray().Count));
        Assert.Equal("used", review.GetProperty("usuallyBought").GetString());
        Assert.Equal(ingredients, review.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task Sync_call_budget_no_available_hit_means_no_history_call()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await EmptyAsync(host);
        await AddIngredientsAsync(owner.Client, household, "x", 2, ["Sy SoldOut", "Sy NoHit"]);
        fake.SoldOutQueries.Add("Sy SoldOut");
        fake.NoHitQueries.Add("Sy NoHit");

        var review = await RunAsync(owner.Client, household);

        Assert.Equal(["product_search"], fake.AllToolNames);
        Assert.Equal("skipped", review.GetProperty("usuallyBought").GetString());
        Assert.Equal("unavailable", Row(review, "Sy SoldOut").GetProperty("reason").GetString());
        Assert.Equal("no-match", Row(review, "Sy NoHit").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Sync_classification_usual_suggestion_not_found_and_options()
    {
        var fake = new FakeOda();
        fake.UsualProductIds.Add(1004); // the second hit for Rice
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host); // Rice (1001, 1004), Egg (1002 sold out, 1003)
        await AddIngredientsAsync(owner.Client, household, "y", 3, ["Sy Fresh", "Sy SoldOut", "Sy NoHit"]);
        fake.SoldOutQueries.Add("Sy SoldOut");
        fake.NoHitQueries.Add("Sy NoHit");

        var review = await RunAsync(owner.Client, household);

        Assert.Equal("done", review.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, review.GetProperty("stopped").ValueKind);
        Assert.Equal("used", review.GetProperty("usuallyBought").GetString());
        Assert.Equal(["Rice"], Names(review, "usually-bought"));
        Assert.Equal(["Egg", "Sy Fresh"], Names(review, "suggestion"));
        Assert.Equal(["Sy NoHit", "Sy SoldOut"], Names(review, "not-found"));
        // Rows are ordered by category.
        Assert.Equal(
            ["usually-bought", "suggestion", "suggestion", "not-found", "not-found"],
            review.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("category").GetString()!).ToArray());

        var rice = Row(review, "Rice");
        Assert.Equal(1004, rice.GetProperty("selectedProductId").GetInt64());
        Assert.True(rice.GetProperty("preticked").GetBoolean());
        Assert.Equal([1001L, 1004L], rice.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("productId").GetInt64()).ToArray());
        Assert.Equal([false, true], rice.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("isUsual").GetBoolean()).ToArray());
        var egg = Row(review, "Egg");
        Assert.Equal(1003, egg.GetProperty("selectedProductId").GetInt64());
        Assert.False(egg.GetProperty("preticked").GetBoolean());
        Assert.Equal(1, egg.GetProperty("options").GetArrayLength()); // the sold-out hit is not offered
        Assert.Equal(JsonValueKind.Null, Row(review, "Sy SoldOut").GetProperty("selectedProductId").ValueKind);
        Assert.False(Row(review, "Sy SoldOut").GetProperty("preticked").GetBoolean());
    }

    [Fact]
    public async Task Sync_re_check_of_an_existing_choice_never_pre_ticks_and_keeps_a_usable_choice_as_current()
    {
        var fake = new FakeOda();
        fake.UsualProductIds.Add(1004);
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host); // Rice (1001, 1004), Egg (1002 sold out, 1003)
        using (var rice = await owner.Client.PutAsync(PrefUrl(household) + $"/{Rice}", new { odaProductId = 1001 }))
        using (var egg = await owner.Client.PutAsync(PrefUrl(household) + $"/{Egg}", new { odaProductId = 1002 })) // sold out now
        {
            Assert.Equal(HttpStatusCode.OK, rice.StatusCode);
            Assert.Equal(HttpStatusCode.OK, egg.StatusCode);
        }

        var review = await RunAsync(owner.Client, household, new { confirmed = true, includeExisting = true });

        var current = Row(review, "Rice");
        Assert.Equal("current", current.GetProperty("category").GetString());
        Assert.True(current.GetProperty("hasExistingChoice").GetBoolean());
        Assert.False(current.GetProperty("preticked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, current.GetProperty("selectedProductId").ValueKind);
        Assert.Equal([1001L], current.GetProperty("options").EnumerateArray().Where(o => o.GetProperty("isCurrent").GetBoolean()).Select(o => o.GetProperty("productId").GetInt64()).ToArray());
        var replaces = Row(review, "Egg");
        Assert.Equal("suggestion", replaces.GetProperty("category").GetString());
        Assert.True(replaces.GetProperty("hasExistingChoice").GetBoolean());
        Assert.False(replaces.GetProperty("preticked").GetBoolean());
        Assert.Equal(1003, replaces.GetProperty("selectedProductId").GetInt64());
    }

    [Theory]
    [InlineData("500")]
    [InlineData("429")]
    [InlineData("timeout")]
    [InlineData("is-error")]
    [InlineData("shape")]
    [InlineData("missing")]
    public async Task Sync_history_failure_falls_back_to_suggestions_and_revokes(string failure)
    {
        var fake = new FakeOda();
        switch (failure)
        {
            case "500":
                fake.FailTool = "likely_to_buy";
                break;
            case "429":
                fake.ToolStatus["likely_to_buy"] = HttpStatusCode.TooManyRequests;
                break;
            case "timeout":
                fake.TimeoutTool = "likely_to_buy";
                break;
            case "is-error":
                fake.ToolIsError.Add("likely_to_buy");
                break;
            case "shape":
                fake.HistoryDamage = "no-result";
                break;
            default:
                fake.OmitTools.Add("likely_to_buy");
                break;
        }

        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        var review = await RunAsync(owner.Client, household);

        Assert.Equal("done", review.GetProperty("status").GetString());
        Assert.Equal("unavailable", review.GetProperty("usuallyBought").GetString());
        Assert.All(
            review.GetProperty("rows").EnumerateArray(),
            r => Assert.Contains(r.GetProperty("category").GetString(), new[] { "suggestion", "not-found" }));
        Assert.True(fake.ToolCalls("likely_to_buy").Count <= 1);
        Assert.NotEmpty(fake.RevokeRequests);
    }

    // ---- Partial results, failures, revoking -------------------------------------------------------------------------

    [Fact]
    public async Task Sync_partial_on_a_usage_limit_returns_the_first_batches_without_a_retry_and_revokes()
    {
        var fake = new FakeOda();
        var searches = 0;
        fake.BeforeTool = name =>
        {
            if (name == "product_search" && ++searches == 3)
            {
                fake.ToolStatus["product_search"] = HttpStatusCode.TooManyRequests;
            }
        };
        await using var host = Host(fake);
        var (owner, household) = await EmptyAsync(host);
        await AddIngredientsAsync(owner.Client, household, "Limit", 45);

        var review = await RunAsync(owner.Client, household);

        Assert.Equal("partial", review.GetProperty("status").GetString());
        Assert.Equal("oda-limit", review.GetProperty("stopped").GetString());
        Assert.Equal(20, review.GetProperty("rows").GetArrayLength());
        Assert.Equal(25, review.GetProperty("notCheckedCount").GetInt32());
        Assert.Equal(3, fake.ToolCalls("product_search").Count); // the third one failed and is not repeated
        Assert.Empty(fake.ToolCalls("likely_to_buy"));
        Assert.Equal(2, fake.RevokeRequests.Count);
    }

    [Fact]
    public async Task Sync_partial_when_the_time_budget_is_spent()
    {
        var fake = new FakeOda { BeforeTool = name => { if (name == "product_search") { Thread.Sleep(1200); } } };
        await using var host = Host(fake, null, ("Oda:SyncBudgetSeconds", "1"));
        var (owner, household) = await EmptyAsync(host);
        await AddIngredientsAsync(owner.Client, household, "Slow", 30);

        var review = await RunAsync(owner.Client, household);

        Assert.Equal("partial", review.GetProperty("status").GetString());
        Assert.Equal("time", review.GetProperty("stopped").GetString());
        Assert.Equal(10, review.GetProperty("rows").GetArrayLength());
        Assert.Equal(20, review.GetProperty("notCheckedCount").GetInt32());
        Assert.Single(fake.ToolCalls("product_search"));
        Assert.NotEmpty(fake.RevokeRequests);
    }

    [Fact]
    public async Task Sync_failure_on_the_first_search_or_a_refused_code_is_a_502_without_rows_or_an_upstream_body()
    {
        foreach (var refusedCode in new[] { false, true })
        {
            var fake = refusedCode ? new FakeOda { TokenStatus = HttpStatusCode.BadRequest } : new FakeOda { FailTool = "product_search" };
            await using var host = Host(fake);
            var (owner, household) = await OdaHost.ScenarioAsync(host);
            var (_, state) = await StartAsync(owner.Client, household);

            using var response = await ReviewAsync(owner.Client, household, state);

            await response.AssertProblemAsync(HttpStatusCode.BadGateway, "oda-unavailable");
            var body = await response.Content.ReadAsStringAsync(Ct);
            var json = JsonDocument.Parse(body).RootElement;
            Assert.False(json.TryGetProperty("rows", out _));
            Assert.Equal(!refusedCode || fake.RevokeRequests.Count == 0, json.GetProperty("connectionClosed").GetBoolean());
            Assert.DoesNotContain("upstream", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret_detail", body, StringComparison.Ordinal);
            if (!refusedCode)
            {
                Assert.NotEmpty(fake.RevokeRequests);
            }
        }
    }

    [Fact]
    public async Task Sync_token_is_revoked_once_after_success_and_the_response_follows_the_revoke()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        var review = await RunAsync(owner.Client, household);

        var revoked = fake.RevokeRequests.Select(r => OdaHost.Form(r.Body)).ToList();
        Assert.Equal(2, revoked.Count); // the access token and the refresh token, once each
        Assert.Contains(revoked, f => f["token"] == fake.AccessToken);
        Assert.Contains(revoked, f => f["token"] == fake.RefreshToken);
        Assert.True(review.GetProperty("connectionClosed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, review.GetProperty("connectionMessage").ValueKind);
    }

    [Fact]
    public async Task Sync_revoke_that_fails_twice_is_reported_with_the_privacy_message()
    {
        var fake = new FakeOda { IssueRefreshToken = false, RevokeFailures = 2 };
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        var review = await RunAsync(owner.Client, household);

        Assert.Equal(2, fake.RevokeRequests.Count);
        Assert.False(review.GetProperty("connectionClosed").GetBoolean());
        Assert.Contains("Privacy & security", review.GetProperty("connectionMessage").GetString(), StringComparison.Ordinal);
        Assert.Equal("done", review.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Sync_token_is_revoked_when_the_request_is_cancelled()
    {
        var fake = new FakeOda();
        using var cancel = new CancellationTokenSource();
        fake.BeforeTool = name =>
        {
            if (name == "product_search")
            {
                cancel.Cancel();
            }
        };
        await using var host = Host(fake);
        using var scope = host.Services.CreateScope();
        var action = scope.ServiceProvider.GetRequiredService<OdaSyncAction>();
        var ingredients = new[] { new SyncIngredient(Guid.NewGuid(), "Rice", 1, false, false) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action.RunAsync(
            Guid.NewGuid(), Guid.NewGuid(), new OdaSecret(OdaHost.Code), new OdaSecret("verifier-value"), ingredients, new Dictionary<Guid, string>(), cancel.Token));

        Assert.Equal(2, fake.RevokeRequests.Count);
        Assert.False(fake.RevokeTokenWasCancelled); // the revoke has its own timeout, not the request's
    }

    // ---- Flow cookie -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sync_flow_cookie_kinds_do_not_complete_each_other_and_every_refusal_looks_the_same()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);
        var shapes = new List<string>();

        // A cart cookie presented to the sync review.
        var (_, cartState) = await OdaHost.StartAsync(owner.Client, household);
        using (var wrongKind = await ReviewAsync(owner.Client, household, cartState))
        {
            await wrongKind.AssertProblemAsync(HttpStatusCode.BadRequest, "oda-state-invalid");
            shapes.Add(await wrongKind.ProblemShapeAsync());
        }

        // A sync cookie presented to the cart action.
        var (_, syncState) = await StartAsync(owner.Client, household);
        using (var wrongKind = await OdaHost.CartAsync(owner.Client, household, syncState))
        {
            await wrongKind.AssertProblemAsync(HttpStatusCode.BadRequest, "oda-state-invalid");
            shapes.Add(await wrongKind.ProblemShapeAsync());
        }

        // Unknown state, reused state and another household.
        var (_, goodState) = await StartAsync(owner.Client, household);
        using (var unknown = await ReviewAsync(owner.Client, household, "unknown-state"))
        {
            await unknown.AssertProblemAsync(HttpStatusCode.BadRequest, "oda-state-invalid");
            shapes.Add(await unknown.ProblemShapeAsync());
        }

        var secondHousehold = await owner.Client.CreateHouseholdAsync("Second");
        var (_, otherState) = await StartAsync(owner.Client, household, new { confirmed = true });
        using (var elsewhere = await ReviewAsync(owner.Client, secondHousehold, otherState))
        {
            await elsewhere.AssertProblemAsync(HttpStatusCode.BadRequest, "oda-state-invalid");
            shapes.Add(await elsewhere.ProblemShapeAsync());
        }

        var (_, reusable) = await StartAsync(owner.Client, household);
        using (var first = await ReviewAsync(owner.Client, household, reusable))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var replay = await ReviewAsync(owner.Client, household, reusable))
        {
            await replay.AssertProblemAsync(HttpStatusCode.BadRequest, "oda-state-invalid");
            shapes.Add(await replay.ProblemShapeAsync());
        }

        Assert.Single(shapes.Distinct());
        Assert.Single(fake.TokenRequests); // only the one good review reached Oda
        Assert.NotEmpty(goodState);
    }

    [Fact]
    public async Task Sync_cancelled_at_oda_is_a_200_without_a_token_or_revoke_and_options_come_from_the_cookie()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        var (_, state) = await StartAsync(owner.Client, household);
        using (var denied = await owner.Client.PostAsync(Url(household, "review"), new { state, error = "access_denied" }))
        {
            Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
            var json = await denied.JsonAsync();
            Assert.Equal("cancelled", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("rows").GetArrayLength());
        }

        Assert.Equal(0, fake.RequestCount);
        using (var again = await owner.Client.PostAsync(Url(household, "review"), new { state, error = "access_denied" }))
        {
            await again.AssertProblemAsync(HttpStatusCode.BadRequest, "oda-state-invalid");
        }

        // A body cannot change the options that were chosen at start (staples were off).
        var (_, plainState) = await StartAsync(owner.Client, household);
        using var plain = await owner.Client.PostAsync(Url(household, "review"), new { state = plainState, code = OdaHost.Code, includeStaples = true, includeExisting = true, offset = 1 });
        var review = await plain.JsonAsync();
        Assert.Equal(["Egg", "Rice"], review.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("ingredientName").GetString()!).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Sync_flow_cookie_of_one_kind_is_refused_for_the_other_and_is_bound_to_user_and_household()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        var cookie = new Foodplanner.Api.Features.ShoppingLists.OdaFlowCookie(Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create("sync-flow-tests"), time);
        var user = Guid.NewGuid();
        var household = Guid.NewGuid();
        var sync = new Foodplanner.Api.Features.ShoppingLists.OdaFlowState(
            "verifier", "state", household, default, user, time.GetUtcNow().AddMinutes(10), Foodplanner.Api.Features.ShoppingLists.OdaFlowKind.Sync, true, false, 30);

        Microsoft.AspNetCore.Http.DefaultHttpContext Issue()
        {
            var issued = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            cookie.Issue(issued.Response, sync);
            var value = issued.Response.Headers.SetCookie.ToString()[(Foodplanner.Api.Features.ShoppingLists.OdaFlowCookie.CookieName.Length + 1)..].Split(';')[0];
            var next = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            next.Request.Headers.Cookie = $"{Foodplanner.Api.Features.ShoppingLists.OdaFlowCookie.CookieName}={value}";
            return next;
        }

        Assert.Null(cookie.Take(Issue(), "state", household, default, user)); // a sync cookie for the cart
        Assert.Null(cookie.Take(Issue(), "state", household, default, Guid.NewGuid(), Foodplanner.Api.Features.ShoppingLists.OdaFlowKind.Sync));
        Assert.Null(cookie.Take(Issue(), "state", Guid.NewGuid(), default, user, Foodplanner.Api.Features.ShoppingLists.OdaFlowKind.Sync));
        var taken = cookie.Take(Issue(), "state", household, default, user, Foodplanner.Api.Features.ShoppingLists.OdaFlowKind.Sync);
        Assert.Equal((true, false, 30), (taken?.IncludeStaples, taken?.IncludeExisting, taken?.Offset));
    }

    // ---- Nothing kept, nothing logged, limits ------------------------------------------------------------------------

    [Fact]
    public async Task Sync_stores_only_the_ticked_product_ids_and_keeps_nothing_else()
    {
        var fake = new FakeOda();
        fake.UsualProductIds.Add(987654321);
        await using var host = Host(fake);
        var (owner, household) = await OdaHost.ScenarioAsync(host);
        await AddGlobalAsync(owner.Client, household, Bacon);

        var (_, state) = await StartAsync(owner.Client, household);
        using var response = await ReviewAsync(owner.Client, household, state);
        var review = await response.JsonAsync();
        var bacon = Row(review, "Bacon");
        Assert.Equal(987654321, bacon.GetProperty("selectedProductId").GetInt64());
        using var save = await BulkAsync(owner.Client, household, (bacon.GetProperty("ingredientId").GetGuid(), 987654321, false));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var verifier = OdaHost.Form(fake.TokenRequests.Single().Body)["code_verifier"];
        var productName = FakeOda.Fixture("product-search.json")["bacon"]![1]!["name"]!.GetValue<string>();
        var hits = await OdaHost.FindInDatabaseAsync(
            db.ConnectionString, fake.AccessToken, fake.RefreshToken, OdaHost.Code, verifier, productName, "Fake History Name", "Fake Usual Product", "oda.com/no/cart");
        Assert.Empty(hits);
        Assert.Equal(1, await RowsAsync(household));
        Assert.Equal(987654321L, await StoredAsync(household, bacon.GetProperty("ingredientId").GetGuid()));
        using var scope = host.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<Foodplanner.Infrastructure.Persistence.AppDbContext>().Model;
        Assert.Equal(
            ["PreferredOdaProduct"],
            model.GetEntityTypes().Where(e => e.ClrType.Name.Contains("Oda", StringComparison.OrdinalIgnoreCase)).Select(e => e.ClrType.Name).ToArray());
    }

    [Fact]
    public async Task Sync_logs_and_problem_bodies_never_carry_ids_names_tokens_codes_or_state()
    {
        var logs = new Foodplanner.Api.Tests.Profiles.CapturingLoggerProvider();
        var fake = new FakeOda();
        fake.UsualProductIds.Add(987654321);
        await using var host = Host(fake, logs);
        var (owner, household) = await OdaHost.ScenarioAsync(host);
        await AddGlobalAsync(owner.Client, household, Bacon);
        var (_, state) = await StartAsync(owner.Client, household);
        using var ok = await ReviewAsync(owner.Client, household, state);
        var okBody = await ok.Content.ReadAsStringAsync(Ct);
        using var save = await BulkAsync(owner.Client, household, (Bacon, 987654321, false));
        var saveBody = await save.Content.ReadAsStringAsync(Ct);
        using var bad = await owner.Client.PutAsync(PrefUrl(household), new { items = new[] { new { ingredientId = Guid.NewGuid(), odaProductId = 987654320L, replace = false } } });
        var badBody = await bad.Content.ReadAsStringAsync(Ct);
        var verifier = OdaHost.Form(fake.TokenRequests.Single().Body)["code_verifier"];

        var failing = new FakeOda { FailTool = "product_search" };
        await using var failHost = Host(failing, logs);
        var (failOwner, failHousehold) = await OdaHost.ScenarioAsync(failHost);
        var (_, failState) = await StartAsync(failOwner.Client, failHousehold);
        using var failed = await ReviewAsync(failOwner.Client, failHousehold, failState);
        var failBody = await failed.Content.ReadAsStringAsync(Ct);

        var names = FakeOda.Fixture("product-search.json")["bacon"]!.AsArray().Select(p => p!["name"]!.GetValue<string>());
        string[] needles = [.. names, "987654321", "987654320", "Fake History Name", fake.AccessToken, fake.RefreshToken, OdaHost.Code, verifier, state, failState, "upstream-error-body-should-not-leak"];
        string[] messages;
        lock (logs.Messages)
        {
            messages = [.. logs.Messages];
        }

        Assert.Contains(messages, m => m.Contains("Oda sync for household", StringComparison.Ordinal)); // not vacuous: the one line is there
        Assert.Contains(messages, m => m.Contains("Oda choices bulk save", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Contains("Bacon", StringComparison.Ordinal) || m.Contains("Rice", StringComparison.Ordinal));
        foreach (var needle in needles)
        {
            Assert.DoesNotContain(messages, m => m.Contains(needle, StringComparison.Ordinal));
        }

        foreach (var needle in needles.Where(n => n != "987654321" && !n.StartsWith("Fake", StringComparison.Ordinal) && n != names.First() && n != names.Last()))
        {
            Assert.DoesNotContain(needle, saveBody + badBody + failBody, StringComparison.Ordinal);
        }

        Assert.Contains("987654321", okBody, StringComparison.Ordinal); // the response is the one place ids and names are
        Assert.DoesNotContain(fake.AccessToken, okBody, StringComparison.Ordinal);
        Assert.DoesNotContain(fake.RefreshToken, okBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_rate_limited_per_user()
    {
        var fake = new FakeOda();
        await using var host = Host(fake, null, ("RateLimiting:Oda:PermitLimit", "2"));
        var (owner, household) = await OdaHost.ScenarioAsync(host);

        for (var i = 0; i < 2; i++)
        {
            using var ok = await owner.Client.PostAsync(Url(household, "start"), new { confirmed = true });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await owner.Client.PostAsync(Url(household, "review"), new { state = "x", code = "y" });
        await limited.AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate-limited");
        Assert.True(limited.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Sync_contract_needs_only_product_search_and_fails_closed_without_it()
    {
        var noCart = new FakeOda { OmitTools = { "manipulate_cart", "get_cart" } };
        await using (var host = Host(noCart))
        {
            var (owner, household) = await OdaHost.ScenarioAsync(host);
            var review = await RunAsync(owner.Client, household);
            Assert.Equal("done", review.GetProperty("status").GetString());
            Assert.DoesNotContain(noCart.AllToolNames, n => n is "manipulate_cart" or "get_cart" or "clear_cart" or "get_orders" or "get_order");

            // The cart action still fails closed without manipulate_cart.
            var (_, cartState) = await OdaHost.StartAsync(owner.Client, household);
            using var cart = await OdaHost.CartAsync(owner.Client, household, cartState);
            await cart.AssertProblemAsync(HttpStatusCode.BadGateway, "oda-unavailable");
        }

        foreach (var broken in new[] { new FakeOda { OmitTools = { "product_search" } }, new FakeOda { OmitArguments = { "product_search.queries" } } })
        {
            await using var host = Host(broken);
            var (owner, household) = await OdaHost.ScenarioAsync(host);
            var (_, state) = await StartAsync(owner.Client, household);
            using var response = await ReviewAsync(owner.Client, household, state);
            await response.AssertProblemAsync(HttpStatusCode.BadGateway, "oda-unavailable");
            Assert.Empty(broken.AllToolNames);
        }
    }

    // ---- Bulk save ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Bulk_save_creates_replaces_only_when_ticked_skips_the_rest_and_leaves_the_same_id_alone()
    {
        await using var host = Host(new FakeOda());
        var (owner, household) = await EmptyAsync(host);

        using (var created = await BulkAsync(owner.Client, household, (Rice, 1001, false), (Egg, 1003, false), (Pasta, 3001, true)))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var json = await created.JsonAsync();
            Assert.Equal((3, 0, 0, 0), (json.GetProperty("created").GetInt32(), json.GetProperty("replaced").GetInt32(), json.GetProperty("unchanged").GetInt32(), json.GetProperty("skippedExisting").GetArrayLength()));
        }

        Assert.Equal(3, await RowsAsync(household));
        var before = await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT UpdatedAt FROM PreferredOdaProducts WHERE HouseholdId = @h AND IngredientId = @i", ("@h", household), ("@i", Egg));

        using (var second = await BulkAsync(owner.Client, household, (Rice, 1004, true), (Egg, 1003, false), (Pasta, 3003, false), (Bacon, 987654320, false)))
        {
            var json = await second.JsonAsync();
            Assert.Equal((1, 1, 1), (json.GetProperty("created").GetInt32(), json.GetProperty("replaced").GetInt32(), json.GetProperty("unchanged").GetInt32()));
            Assert.Equal([Pasta], json.GetProperty("skippedExisting").EnumerateArray().Select(e => e.GetGuid()).ToArray());
        }

        Assert.Equal(1004L, await StoredAsync(household, Rice));
        Assert.Equal(3001L, await StoredAsync(household, Pasta)); // skipped: unchanged
        Assert.Equal(987654320L, await StoredAsync(household, Bacon));
        Assert.Equal(
            before,
            await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT UpdatedAt FROM PreferredOdaProducts WHERE HouseholdId = @h AND IngredientId = @i", ("@h", household), ("@i", Egg))); // no write
        Assert.Equal(4, await RowsAsync(household));
    }

    [Fact]
    public async Task Bulk_save_the_501st_row_is_refused_and_nothing_is_saved_but_a_replace_at_the_limit_works()
    {
        await using var host = Host(new FakeOda());
        var (owner, household) = await EmptyAsync(host);
        await HouseholdScenario.ScalarAsync(db.ConnectionString, """
            DECLARE @now datetimeoffset = SYSDATETIMEOFFSET();
            INSERT INTO Ingredients (Id, HouseholdId, Name, IsStaple, Dimension, Section, CreatedAt, UpdatedAt)
            SELECT TOP 500 NEWID(), @h, N'Limit ' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS nvarchar(10)), 0, 'Mass', 'Other', @now, @now
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            INSERT INTO PreferredOdaProducts (Id, HouseholdId, IngredientId, OdaProductId, CreatedAt, UpdatedAt)
            SELECT NEWID(), @h, Id, 7000, @now, @now FROM Ingredients WHERE HouseholdId = @h;
            """, ("@h", household));
        var existing = (Guid)(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT TOP 1 IngredientId FROM PreferredOdaProducts WHERE HouseholdId = @h", ("@h", household)))!;

        using var refused = await BulkAsync(owner.Client, household, (existing, 7001, true), (Rice, 1001, false));
        using var replaced = await BulkAsync(owner.Client, household, (existing, 7002, true));

        await refused.AssertProblemAsync(HttpStatusCode.Conflict, "oda-preference-limit");
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(500, await RowsAsync(household));
        Assert.Equal(7002L, await StoredAsync(household, existing)); // the refused call changed nothing (not even its replace)
        Assert.Null(await StoredAsync(household, Rice));
    }

    [Fact]
    public async Task Bulk_save_validates_the_body_on_the_fields()
    {
        await using var host = Host(new FakeOda());
        var (owner, household) = await EmptyAsync(host);

        async Task AssertFieldAsync(object? body, string key)
        {
            using var response = await owner.Client.PutAsync(PrefUrl(household), body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty(key, out _), $"expected an error for '{key}'");
        }

        await AssertFieldAsync(new { }, "items");
        await AssertFieldAsync(new { items = Array.Empty<object>() }, "items");
        await AssertFieldAsync(new { items = Enumerable.Range(1, 201).Select(i => new { ingredientId = Guid.NewGuid(), odaProductId = (long)i, replace = false }).ToArray() }, "items");
        await AssertFieldAsync(new { items = new[] { new { ingredientId = Rice, odaProductId = 1L, replace = false }, new { ingredientId = Rice, odaProductId = 2L, replace = false } } }, "items[1].ingredientId");
        await AssertFieldAsync(new { items = new[] { new { ingredientId = Guid.Empty, odaProductId = 1L, replace = false } } }, "items[0].ingredientId");
        foreach (var bad in new object?[] { 0, -5, 1.5, "7", null })
        {
            await AssertFieldAsync(new { items = new[] { new { ingredientId = Rice, odaProductId = bad, replace = false } } }, "items[0].odaProductId");
        }

        await AssertFieldAsync(new { items = new[] { new { ingredientId = Rice, replace = false } } }, "items[0].odaProductId");
        await AssertFieldAsync(new { items = new[] { new { ingredientId = Rice, odaProductId = 1L, replace = (object)"yes" } } }, "items[0].replace");
        Assert.Equal(0, await RowsAsync(household));
    }

    [Fact]
    public async Task Bulk_save_an_unknown_or_foreign_ingredient_anywhere_is_the_same_404_and_nothing_is_saved()
    {
        await using var host = Host(new FakeOda());
        var (owner, household) = await EmptyAsync(host);
        var (otherOwner, other) = await EmptyAsync(host);
        var foreign = (await AddIngredientsAsync(otherOwner.Client, other, "Theirs", 1)).Single();

        using var foreignResponse = await BulkAsync(owner.Client, household, (Rice, 1001, false), (foreign, 5, false));
        using var unknownResponse = await BulkAsync(owner.Client, household, (Rice, 1001, false), (Guid.NewGuid(), 5, false));

        await foreignResponse.AssertProblemAsync(HttpStatusCode.NotFound, "ingredient-not-found");
        await unknownResponse.AssertProblemAsync(HttpStatusCode.NotFound, "ingredient-not-found");
        Assert.Equal(await foreignResponse.ProblemShapeAsync(), await unknownResponse.ProblemShapeAsync());
        Assert.Equal(0, await RowsAsync(household));
        Assert.Equal(0, await RowsAsync(other));
        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }

    [Fact]
    public async Task Bulk_save_concurrent_identical_calls_end_in_one_row_per_ingredient_and_no_server_error()
    {
        await using var host = Host(new FakeOda());
        var (owner, household) = await EmptyAsync(host);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => BulkAsync(owner.Client, household, (Rice, 1001, false), (Egg, 1003, false))));

        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(2, await RowsAsync(household));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Sync_tenant_isolation_a_sync_and_a_bulk_save_never_touch_another_households_rows()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (ownerA, a) = await OdaHost.ScenarioAsync(host);
        var (ownerB, b) = await OdaHost.ScenarioAsync(host);
        using (var seeded = await BulkAsync(ownerB.Client, b, (Rice, 4242, false)))
        {
            Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);
        }

        var review = await RunAsync(ownerA.Client, a); // B has a choice for Rice; A's run is not affected by it
        Assert.Equal(2, review.GetProperty("rows").GetArrayLength());
        using (var save = await BulkAsync(ownerA.Client, a, (Rice, 1001, true)))
        {
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        }

        Assert.Equal(4242L, await StoredAsync(b, Rice));
        Assert.Equal(1001L, await StoredAsync(a, Rice));
        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }
}
