using System.Net;
using System.Text.Json;
using Foodplanner.Api.Authorization;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.Profiles;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Catalog;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Oda;

/// <summary>T-052: the household's chosen Oda product per ingredient (endpoints, tenancy, merge and delete) and how the cart fill uses it.</summary>
public sealed class OdaPreferenceTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly Guid Salt = Seed("salt");
    private static readonly Guid Rice = Seed("ris");
    private static readonly Guid Egg = Seed("egg");
    private static readonly Guid Pasta = Seed("pasta");
    private static readonly Guid Bacon = Seed("bacon");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid Seed(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    private AuthHost Host(FakeOda? fake = null, Foodplanner.Api.Tests.Profiles.CapturingLoggerProvider? logs = null) =>
        OdaHost.Create(db.ConnectionString, fake ?? new FakeOda(), logs);

    private static string Url(Guid household, string rest = "") => $"/api/households/{household}/oda-preferences{rest}";

    private static Task<HttpResponseMessage> PutAsync(ApiClient client, Guid household, Guid ingredient, object? body) =>
        client.PutAsync(Url(household, $"/{ingredient}"), body);

    private static Task<HttpResponseMessage> SetAsync(ApiClient client, Guid household, Guid ingredient, long productId) =>
        PutAsync(client, household, ingredient, new { odaProductId = productId });

    private async Task<long> RowsAsync(Guid household) =>
        Convert.ToInt64(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM PreferredOdaProducts WHERE HouseholdId = @h", ("@h", household)));

    private async Task<long?> StoredAsync(Guid household, Guid ingredient) =>
        await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT OdaProductId FROM PreferredOdaProducts WHERE HouseholdId = @h AND IngredientId = @i", ("@h", household), ("@i", ingredient)) as long?;

    private async Task AssertNoCrossHouseholdLinksAsync()
    {
        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }

    /// <summary>An Owner whose list holds the global ingredients (500 g each): the first names, in the order of the recipe.</summary>
    private static async Task<(SignedInUser Owner, Guid Household)> ListScenarioAsync(AuthHost host, params Guid[] ingredients)
    {
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        owner.Client.Language = "en";
        await PlanAsync(owner, household, ingredients);
        return (owner, household);
    }

    private static async Task PlanAsync(SignedInUser owner, Guid household, Guid[] ingredients)
    {
        var gram = UnitSeed.All.Single(r => r.Code == "g").Id;
        using var recipe = await owner.Client.PostAsync($"/api/households/{household}/recipes", new
        {
            title = "Pick " + Guid.NewGuid().ToString("N")[..6],
            servings = 4,
            ingredients = ingredients.Select(i => new { ingredientId = i, quantity = (decimal?)500, unitId = (Guid?)gram, note = (string?)null }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Created, recipe.StatusCode);
        var recipeId = (await recipe.JsonAsync()).GetProperty("id").GetGuid();
        using var entry = await owner.Client.PostAsync(
            $"/api/households/{household}/meal-plans/{OdaHost.Monday}/entries", new { date = OdaHost.Monday, recipeId, servings = 4 });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        using var generated = await owner.Client.PostAsync($"/api/households/{household}/shopping-lists/{OdaHost.Monday}/generate");
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
    }

    private static Dictionary<string, JsonElement> AddedByName(JsonElement result) =>
        result.GetProperty("added").EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!, a => a);

    private static long[] AddedProductIds(FakeOda fake) =>
        [.. fake.ToolCalls("manipulate_cart").SelectMany(c => c["operations"]!.AsArray()).Select(o => o!["productId"]!.GetValue<long>())];

    // ---- Roles and policies ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Preference_roles_owner_and_adult_manage_everyone_else_is_refused()
    {
        await using var host = Host();
        var kid = await host.NewScenarioAsync();
        var household = kid.Household;
        var adult = await host.JoinAsync(kid.Owner, household, "Adult");
        var child = await host.JoinAsync(kid.Owner, household, "Child");
        var stranger = await host.SignInAsync();
        await stranger.Client.CreateHouseholdAsync();
        var anonymous = host.CreateClient();

        foreach (var client in new[] { kid.Owner.Client, adult.Client })
        {
            using var put = await SetAsync(client, household, Salt, 111);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            using var list = await client.GetAsync(Url(household));
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var delete = await client.DeleteAsync(Url(household, $"/{Salt}"));
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        using var seeded = await SetAsync(kid.Owner.Client, household, Egg, 222);
        Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);

        async Task AssertRefusedAsync(ApiClient client, HttpStatusCode status, string? code)
        {
            using var get = await client.GetAsync(Url(household));
            using var put = await SetAsync(client, household, Salt, 333);
            using var delete = await client.DeleteAsync(Url(household, $"/{Egg}"));
            foreach (var response in new[] { get, put, delete })
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

        Assert.Equal(1, await RowsAsync(household)); // the Child's, the stranger's and kid mode's PUT/DELETE changed nothing
        Assert.Equal(222, await StoredAsync(household, Egg));
        Assert.Null(await StoredAsync(household, Salt));
    }

    [Fact]
    public async Task Preference_endpoints_are_adult_only_full_session_and_antiforgery_protected()
    {
        await using var host = Host();

        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.Contains("/oda-preferences", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            [
                "DELETE /api/households/{householdId:guid}/oda-preferences/{ingredientId:guid}",
                "GET /api/households/{householdId:guid}/oda-preferences",
                "PUT /api/households/{householdId:guid}/oda-preferences", // T-130: the bulk save
                "PUT /api/households/{householdId:guid}/oda-preferences/{ingredientId:guid}",
            ],
            endpoints.SelectMany(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Select(m => $"{m} {e.RoutePattern.RawText!.TrimEnd('/')}")).Order(StringComparer.Ordinal).ToArray());
        foreach (var endpoint in endpoints)
        {
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
            Assert.Contains(Policies.HouseholdAdult, policies);
            Assert.Contains(Policies.FullSession, policies);
            Assert.NotNull(endpoint.Metadata.GetMetadata<Foodplanner.Api.Security.AntiforgeryValidatedMetadata>());
        }
    }

    // ---- Tenancy -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Preference_tenant_isolation_other_households_ingredient_is_unknown_and_its_choices_are_not_used()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (ownerA, householdA) = await ListScenarioAsync(host, Rice);
        var (ownerB, householdB) = await ListScenarioAsync(host, Rice);
        using var ownIngredient = await ownerB.Client.PostAsync($"/api/households/{householdB}/ingredients", new { name = "Isolation " + Guid.NewGuid().ToString("N")[..8], isStaple = false, dimension = "Mass" });
        var bOwn = (await ownIngredient.JsonAsync()).GetProperty("id").GetGuid();
        (await SetAsync(ownerB.Client, householdB, Rice, 1004)).Dispose(); // B chose the second rice, A has no choice

        using var getUnderB = await ownerA.Client.GetAsync(Url(householdB));
        using var putUnderB = await SetAsync(ownerA.Client, householdB, Rice, 1001);
        using var deleteUnderB = await ownerA.Client.DeleteAsync(Url(householdB, $"/{Rice}"));
        foreach (var response in new[] { getUnderB, putUnderB, deleteUnderB })
        {
            await response.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using var putForeign = await SetAsync(ownerA.Client, householdA, bOwn, 1001);
        using var putUnknown = await SetAsync(ownerA.Client, householdA, Guid.NewGuid(), 1001);
        using var deleteForeign = await ownerA.Client.DeleteAsync(Url(householdA, $"/{bOwn}"));
        using var deleteUnknown = await ownerA.Client.DeleteAsync(Url(householdA, $"/{Guid.NewGuid()}"));
        foreach (var response in new[] { putForeign, putUnknown, deleteForeign, deleteUnknown })
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "ingredient-not-found");
        }

        Assert.Equal(0, await RowsAsync(householdA));
        Assert.Equal(1004, await StoredAsync(householdB, Rice));
        await AssertNoCrossHouseholdLinksAsync();

        var result = await OdaHost.StartAndCompleteAsync(ownerA.Client, householdA);

        Assert.Equal([1001L], AddedProductIds(fake)); // B's choice (1004) is not A's
        Assert.Equal("first-hit", AddedByName(result)["Rice"].GetProperty("source").GetString());
        Assert.False(AddedByName(result)["Rice"].GetProperty("hasPreference").GetBoolean());
    }

    [Fact]
    public async Task Preference_stores_only_the_id()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await ListScenarioAsync(host, Pasta, Rice);
        using var set = await SetAsync(owner.Client, household, Pasta, 3005);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        var result = await OdaHost.StartAndCompleteAsync(owner.Client, household);

        Assert.Equal("preferred", AddedByName(result)["Pasta"].GetProperty("source").GetString());
        using var scope = host.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<AppDbContext>().Model;
        var odaEntities = model.GetEntityTypes().Where(e => e.ClrType.Name.Contains("Oda", StringComparison.OrdinalIgnoreCase)).ToList();
        var entity = Assert.Single(odaEntities);
        Assert.Equal("PreferredOdaProduct", entity.ClrType.Name);
        Assert.Equal(
            ["CreatedAt", "HouseholdId", "Id", "IngredientId", "OdaProductId", "UpdatedAt"],
            entity.GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        var hits = await OdaHost.FindInDatabaseAsync(
            db.ConnectionString, "Fake Pasta", "Fake Basmatiris", "Fake Jasminris", "oda.com/no", "39.90", fake.AccessToken, fake.RefreshToken);
        Assert.Empty(hits);
    }

    // ---- Set, replace, clear, list -----------------------------------------------------------------------------------

    [Fact]
    public async Task Preference_set_replace_clear()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        owner.Client.Language = "en";

        using var created = await SetAsync(owner.Client, household, Rice, 1001);
        var createdBody = await created.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(Rice, createdBody.GetProperty("ingredientId").GetGuid());
        Assert.Equal("Rice", createdBody.GetProperty("ingredientName").GetString());
        Assert.False(createdBody.TryGetProperty("odaProductId", out _));
        Assert.Equal(1001, await StoredAsync(household, Rice));

        using var replaced = await SetAsync(owner.Client, household, Rice, 1004);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(1, await RowsAsync(household));
        Assert.Equal(1004, await StoredAsync(household, Rice));

        using var again = await SetAsync(owner.Client, household, Rice, 1004);
        var againBody = await again.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await RowsAsync(household));
        Assert.Equal(
            (await replaced.JsonAsync()).GetProperty("setAt").GetDateTimeOffset(),
            againBody.GetProperty("setAt").GetDateTimeOffset()); // nothing visible changed

        using var removed = await owner.Client.DeleteAsync(Url(household, $"/{Rice}"));
        using var removedAgain = await owner.Client.DeleteAsync(Url(household, $"/{Rice}"));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, removedAgain.StatusCode);
        Assert.Equal(0, await RowsAsync(household));
    }

    [Fact]
    public async Task Preference_set_validates_the_product_id_on_the_field()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        foreach (var body in new object?[] { new { }, new { odaProductId = 0 }, new { odaProductId = -5 }, new { odaProductId = 1.5 }, new { odaProductId = 9223372036854775808m }, new { odaProductId = "abc" }, new { odaProductId = (string?)null }, null })
        {
            using var response = await PutAsync(owner.Client, household, Rice, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("odaProductId", out _), "expected an error for odaProductId");
        }

        Assert.Equal(0, await RowsAsync(household));
    }

    [Fact]
    public async Task Preference_501st_choice_is_refused_but_a_replace_still_works()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await HouseholdScenario.ScalarAsync(db.ConnectionString, """
            DECLARE @now datetimeoffset = SYSDATETIMEOFFSET();
            INSERT INTO Ingredients (Id, HouseholdId, Name, IsStaple, Dimension, Section, CreatedAt, UpdatedAt)
            SELECT TOP 500 NEWID(), @h, N'Limit ' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS nvarchar(10)), 0, 'Mass', 'Other', @now, @now
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            INSERT INTO PreferredOdaProducts (Id, HouseholdId, IngredientId, OdaProductId, CreatedAt, UpdatedAt)
            SELECT NEWID(), @h, Id, 7000, @now, @now FROM Ingredients WHERE HouseholdId = @h;
            """, ("@h", household));
        Assert.Equal(500, await RowsAsync(household));
        var existing = (Guid)(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT TOP 1 IngredientId FROM PreferredOdaProducts WHERE HouseholdId = @h", ("@h", household)))!;

        using var refused = await SetAsync(owner.Client, household, Rice, 1001);
        using var replaced = await SetAsync(owner.Client, household, existing, 7001);

        await refused.AssertProblemAsync(HttpStatusCode.Conflict, "oda-preference-limit");
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(500, await RowsAsync(household));
        Assert.Equal(7001, await StoredAsync(household, existing));
    }

    [Fact]
    public async Task Preference_concurrent_sets_for_one_ingredient_end_in_one_row_and_no_server_error()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        var responses = await Task.WhenAll(Enumerable.Range(1, 8).Select(i => SetAsync(owner.Client, household, Rice, 100 + i)));

        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, await RowsAsync(household));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Preference_list_is_name_ordered_in_the_request_language_without_product_data()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var other = await host.SignInAsync();
        var otherHousehold = await other.Client.CreateHouseholdAsync();
        using var empty = await owner.Client.GetAsync(Url(household));
        Assert.Equal("[]", await empty.Content.ReadAsStringAsync(Ct));
        (await SetAsync(owner.Client, household, Salt, 301)).Dispose();
        (await SetAsync(owner.Client, household, Rice, 302)).Dispose();
        (await SetAsync(owner.Client, household, Egg, 303)).Dispose();
        (await SetAsync(other.Client, otherHousehold, Pasta, 304)).Dispose();

        owner.Client.Language = "en";
        using var english = await owner.Client.GetAsync(Url(household));
        owner.Client.Language = "nb";
        using var norwegian = await owner.Client.GetAsync(Url(household));

        var rows = (await english.JsonAsync()).EnumerateArray().ToList();
        Assert.Equal(["Egg", "Rice", "Salt"], rows.Select(r => r.GetProperty("ingredientName").GetString()).ToArray());
        Assert.Equal(["Egg", "Ris", "Salt"], (await norwegian.JsonAsync()).EnumerateArray().Select(r => r.GetProperty("ingredientName").GetString()).ToArray());
        Assert.All(rows, r => Assert.Equal(["ingredientId", "ingredientName", "setAt"], r.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray()));
        Assert.All(rows, r => Assert.True(r.GetProperty("setAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5)));
        Assert.DoesNotContain("Pasta", await english.Content.ReadAsStringAsync(Ct));
    }

    // ---- Merge and delete --------------------------------------------------------------------------------------------

    private static async Task<Guid> CreateOwnAsync(ApiClient client, Guid household, string prefix)
    {
        using var response = await client.PostAsync($"/api/households/{household}/ingredients", new { name = prefix + Guid.NewGuid().ToString("N")[..10], isStaple = false, dimension = "Mass" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Preference_ingredient_merge_moves_or_drops_the_row_and_keeps_the_index_intact()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var movedSource = await CreateOwnAsync(owner.Client, household, "Moved");
        var movedTarget = await CreateOwnAsync(owner.Client, household, "Target");
        var keptSource = await CreateOwnAsync(owner.Client, household, "Source");
        var keptTarget = await CreateOwnAsync(owner.Client, household, "Kept");
        (await SetAsync(owner.Client, household, movedSource, 11)).Dispose();
        (await SetAsync(owner.Client, household, keptSource, 21)).Dispose();
        (await SetAsync(owner.Client, household, keptTarget, 22)).Dispose();

        using var moved = await owner.Client.PostAsync($"/api/households/{household}/ingredients/{movedSource}/merge", new { targetIngredientId = movedTarget });
        using var kept = await owner.Client.PostAsync($"/api/households/{household}/ingredients/{keptSource}/merge", new { targetIngredientId = keptTarget });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(11, await StoredAsync(household, movedTarget));
        Assert.Null(await StoredAsync(household, movedSource));
        Assert.Equal(22, await StoredAsync(household, keptTarget)); // the target's own choice stays
        Assert.Null(await StoredAsync(household, keptSource));
        Assert.Equal(2, await RowsAsync(household));
    }

    [Fact]
    public async Task Preference_is_removed_with_its_ingredient_in_the_same_save_and_does_not_block_the_delete()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var own = await CreateOwnAsync(owner.Client, household, "Gone");
        (await SetAsync(owner.Client, household, own, 55)).Dispose();
        Assert.Equal(1, await RowsAsync(household));

        using var deleted = await owner.Client.DeleteAsync($"/api/households/{household}/ingredients/{own}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await RowsAsync(household));
    }

    // ---- The fill ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Fill_priority_order_preferred_then_usual_then_first_hit()
    {
        var fake = new FakeOda();
        fake.UsualProductIds.Add(3003); // ranks above the pick, but the pick wins
        fake.UsualProductIds.Add(1004); // Rice has no pick: the usual product wins over the first hit
        await using var host = Host(fake);
        var (owner, household) = await ListScenarioAsync(host, Pasta, Rice, Egg);
        await OdaHost.AddManualAsync(owner.Client, household, "Smør"); // a manual item has no ingredient: no pick, first hit
        (await SetAsync(owner.Client, household, Pasta, 3005)).Dispose();

        var result = await OdaHost.StartAndCompleteAsync(owner.Client, household);

        var added = AddedByName(result);
        Assert.Equal("preferred", added["Pasta"].GetProperty("source").GetString());
        Assert.Equal(3005, added["Pasta"].GetProperty("productId").GetInt64());
        Assert.Equal("usually-bought", added["Rice"].GetProperty("source").GetString());
        Assert.Equal("first-hit", added["Egg"].GetProperty("source").GetString());
        Assert.Equal("first-hit", added["Smør"].GetProperty("source").GetString());
        Assert.True(added["Pasta"].GetProperty("hasPreference").GetBoolean());
        Assert.Equal(JsonValueKind.Null, added["Pasta"].GetProperty("preferenceProblem").ValueKind);
        Assert.Equal(JsonValueKind.Null, added["Smør"].GetProperty("ingredientId").ValueKind);
        Assert.Equal(Pasta, added["Pasta"].GetProperty("ingredientId").GetGuid());
        var ids = AddedProductIds(fake);
        Assert.Contains(3005L, ids);
        Assert.DoesNotContain(3003L, ids);
        Assert.Equal(1, result.GetProperty("usuallyBought").GetProperty("count").GetInt32());
        Assert.Equal("used", result.GetProperty("usuallyBought").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Preference_unavailable_or_gone_falls_back_reports_why_and_keeps_the_row()
    {
        var fake = new FakeOda();
        fake.UsualProductIds.Add(3005);
        await using var host = Host(fake);
        var (owner, household) = await ListScenarioAsync(host, Pasta, Rice);
        (await SetAsync(owner.Client, household, Pasta, 3002)).Dispose(); // in the hits, sold out
        (await SetAsync(owner.Client, household, Rice, 424242)).Dispose(); // not in the hits at all

        var result = await OdaHost.StartAndCompleteAsync(owner.Client, household);

        var added = AddedByName(result);
        Assert.Equal("unavailable", added["Pasta"].GetProperty("preferenceProblem").GetString());
        Assert.Equal("usually-bought", added["Pasta"].GetProperty("source").GetString()); // history still applies
        Assert.Equal(3005, added["Pasta"].GetProperty("productId").GetInt64());
        Assert.Equal("not-found", added["Rice"].GetProperty("preferenceProblem").GetString());
        Assert.Equal("first-hit", added["Rice"].GetProperty("source").GetString());
        Assert.Equal(1001, added["Rice"].GetProperty("productId").GetInt64());
        Assert.True(added["Rice"].GetProperty("hasPreference").GetBoolean());
        Assert.Equal(3002, await StoredAsync(household, Pasta)); // never changed or deleted by the fill
        Assert.Equal(424242, await StoredAsync(household, Rice));

        var soldOut = new FakeOda();
        soldOut.SoldOut.Add(3005);
        await using var soldOutHost = Host(soldOut);
        var (soldOutOwner, soldOutHousehold) = await ListScenarioAsync(soldOutHost, Pasta);
        (await SetAsync(soldOutOwner.Client, soldOutHousehold, Pasta, 3005)).Dispose();
        var second = await OdaHost.StartAndCompleteAsync(soldOutOwner.Client, soldOutHousehold);
        Assert.Equal("unavailable", AddedByName(second)["Pasta"].GetProperty("preferenceProblem").GetString());
        Assert.Equal(3001, AddedByName(second)["Pasta"].GetProperty("productId").GetInt64());
        Assert.Equal(3005, await StoredAsync(soldOutHousehold, Pasta));
    }

    [Fact]
    public async Task Preference_with_no_available_hit_is_not_added_and_not_mentioned()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await ListScenarioAsync(host, Seed("melk"));
        owner.Client.Language = "nb"; // the fake knows "melk"
        (await SetAsync(owner.Client, household, Seed("melk"), 1005)).Dispose();

        var result = await OdaHost.StartAndCompleteAsync(owner.Client, household);

        Assert.Equal(0, result.GetProperty("added").GetArrayLength());
        var notAdded = result.GetProperty("notAdded").EnumerateArray().Single();
        Assert.Equal("unavailable", notAdded.GetProperty("reason").GetString());
        Assert.DoesNotContain("preference", notAdded.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1005, await StoredAsync(household, Seed("melk")));
    }

    [Fact]
    public async Task Result_alternatives_shape()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var (owner, household) = await ListScenarioAsync(host, Pasta, Rice);
        await OdaHost.AddManualAsync(owner.Client, household, "Smør");
        (await SetAsync(owner.Client, household, Pasta, 3006)).Dispose();

        var result = await OdaHost.StartAndCompleteAsync(owner.Client, household);

        var pasta = AddedByName(result)["Pasta"].GetProperty("alternatives").EnumerateArray().ToList();
        Assert.Equal([3001L, 3003, 3005, 3006, 3007], pasta.Select(a => a.GetProperty("productId").GetInt64()).ToArray()); // 8 hits, two sold out: five available, in rank order
        Assert.All(pasta, a => Assert.StartsWith("Fake Pasta", a.GetProperty("productName").GetString(), StringComparison.Ordinal));
        Assert.DoesNotContain(pasta, a => a.GetProperty("productName").GetString()!.Contains("utsolgt", StringComparison.Ordinal));
        Assert.Equal([3006L], pasta.Where(a => a.GetProperty("isAdded").GetBoolean()).Select(a => a.GetProperty("productId").GetInt64()).ToArray());
        Assert.Equal([3006L], pasta.Where(a => a.GetProperty("isPreferred").GetBoolean()).Select(a => a.GetProperty("productId").GetInt64()).ToArray());
        var rice = AddedByName(result)["Rice"].GetProperty("alternatives").EnumerateArray().ToList();
        Assert.Equal(2, rice.Count);
        Assert.Single(rice, a => a.GetProperty("isAdded").GetBoolean());
        Assert.DoesNotContain(rice, a => a.GetProperty("isPreferred").GetBoolean());
        Assert.Equal(0, AddedByName(result)["Smør"].GetProperty("alternatives").GetArrayLength());

        var beyond = new FakeOda();
        beyond.UsualProductIds.Add(3008); // ranked 8th: still always offered
        await using var beyondHost = Host(beyond);
        var (beyondOwner, beyondHousehold) = await ListScenarioAsync(beyondHost, Pasta);
        var second = await OdaHost.StartAndCompleteAsync(beyondOwner.Client, beyondHousehold);
        var alternatives = AddedByName(second)["Pasta"].GetProperty("alternatives").EnumerateArray().ToList();
        Assert.Equal([3001L, 3003, 3005, 3006, 3008], alternatives.Select(a => a.GetProperty("productId").GetInt64()).ToArray());
        Assert.Equal([3008L], alternatives.Where(a => a.GetProperty("isAdded").GetBoolean()).Select(a => a.GetProperty("productId").GetInt64()).ToArray());
    }

    [Fact]
    public async Task A_failed_fill_lists_what_was_added_without_alternatives()
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
        var (owner, household) = await ListScenarioAsync(host, Pasta);
        for (var i = 1; i <= 10; i++)
        {
            await OdaHost.AddManualAsync(owner.Client, household, $"Item {i}");
        }

        var (_, state) = await OdaHost.StartAsync(owner.Client, household);
        using var response = await OdaHost.CartAsync(owner.Client, household, state);

        await response.AssertProblemAsync(HttpStatusCode.BadGateway, "oda-unavailable");
        var added = (await response.JsonAsync()).GetProperty("added").EnumerateArray().ToList();
        Assert.Equal(10, added.Count);
        Assert.All(added, a =>
        {
            Assert.Contains(a.GetProperty("source").GetString(), new[] { "preferred", "usually-bought", "first-hit" });
            Assert.Equal(0, a.GetProperty("alternatives").GetArrayLength());
        });
    }

    [Fact]
    public async Task Fill_call_counts_with_many_items_and_with_every_item_decided_by_a_choice()
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

        await OdaHost.StartAndCompleteAsync(owner.Client, household);

        Assert.Equal(
            ["product_search", "product_search", "product_search", "likely_to_buy", "manipulate_cart", "manipulate_cart", "manipulate_cart", "get_cart"],
            fake.AllToolNames);

        var decided = new FakeOda();
        await using var decidedHost = Host(decided);
        var (decidedOwner, decidedHousehold) = await ListScenarioAsync(decidedHost, Pasta, Rice);
        (await SetAsync(decidedOwner.Client, decidedHousehold, Pasta, 3005)).Dispose();
        (await SetAsync(decidedOwner.Client, decidedHousehold, Rice, 1004)).Dispose();
        var requestsBefore = decided.RequestCount;

        var result = await OdaHost.StartAndCompleteAsync(decidedOwner.Client, decidedHousehold);

        Assert.True(decided.RequestCount > requestsBefore);
        Assert.Equal(["product_search", "manipulate_cart", "get_cart"], decided.AllToolNames); // no likely_to_buy, nothing searched by id
        Assert.Equal("skipped", result.GetProperty("usuallyBought").GetProperty("status").GetString());
        Assert.Equal([1004L, 3005L], AddedProductIds(decided).Order().ToArray());
    }

    [Fact]
    public async Task Preference_endpoints_never_call_oda()
    {
        var fake = new FakeOda();
        await using var host = Host(fake);
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        (await SetAsync(owner.Client, household, Rice, 1001)).Dispose();
        (await owner.Client.GetAsync(Url(household))).Dispose();
        (await owner.Client.DeleteAsync(Url(household, $"/{Rice}"))).Dispose();

        Assert.Equal(0, fake.RequestCount);
    }

    [Fact]
    public async Task Preference_product_ids_and_names_are_never_logged_or_leaked_in_problems()
    {
        var logs = new Foodplanner.Api.Tests.Profiles.CapturingLoggerProvider();
        var fake = new FakeOda();
        await using var host = Host(fake, logs);
        var (owner, household) = await ListScenarioAsync(host, Bacon);
        using var set = await SetAsync(owner.Client, household, Bacon, 987654321);
        using var invalid = await PutAsync(owner.Client, household, Bacon, new { odaProductId = -987654321 });
        using var foreign = await SetAsync(owner.Client, household, Guid.NewGuid(), 987654321);
        var bodies = new List<string>
        {
            await set.Content.ReadAsStringAsync(Ct),
            await invalid.Content.ReadAsStringAsync(Ct),
            await foreign.Content.ReadAsStringAsync(Ct),
        };

        var result = await OdaHost.StartAndCompleteAsync(owner.Client, household);

        Assert.Equal("preferred", AddedByName(result)["Bacon"].GetProperty("source").GetString()); // not vacuous: the id was used
        string[] messages;
        lock (logs.Messages)
        {
            messages = [.. logs.Messages];
        }

        Assert.Contains(messages, m => m.Contains("preferred 1", StringComparison.Ordinal)); // the one line carries only a count
        foreach (var needle in new[] { "987654321", "987654320", "Fake Recognisable Bacon" })
        {
            Assert.DoesNotContain(messages, m => m.Contains(needle, StringComparison.Ordinal));
            Assert.All(bodies, b => Assert.DoesNotContain(needle, b, StringComparison.Ordinal));
        }
    }
}
