using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Catalog;

namespace Foodplanner.Api.Tests.ShoppingLists;

public sealed class PantryEndpointTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly Guid Rice = Ingredient("ris"); // not a staple
    private static readonly Guid Salt = Ingredient("salt"); // staple
    private static readonly Guid Flour = Ingredient("hvetemel"); // staple
    private static readonly Guid Gram = IngredientUnit("g");

    private static Guid Ingredient(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    private static Guid IngredientUnit(string code) => UnitSeed.All.Single(r => r.Code == code).Id;

    private AuthHost Host()
    {
        var host = HouseholdHost.Create(db.ConnectionString);
        host.DefaultLanguage = "nb";
        return host;
    }

    private static string Url(Guid household, string rest = "") => $"/api/households/{household}/pantry{rest}";

    private static string ListUrl(Guid household, string rest = "") =>
        $"/api/households/{household}/shopping-lists/{Monday:yyyy-MM-dd}{rest}";

    private static async Task AssertValidationAsync(HttpResponseMessage response, string key)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty(key, out _), $"expected an error for '{key}'");
    }

    private async Task AssertNoCrossHouseholdLinksAsync()
    {
        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }

    /// <summary>A household whose plan for the week uses rice (main) and salt and flour (staples).</summary>
    private static async Task<(SignedInUser Owner, Guid Household)> ScenarioAsync(AuthHost host)
    {
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        using var recipe = await owner.Client.PostAsync($"/api/households/{household}/recipes", new
        {
            title = "Pancakes",
            servings = 4,
            ingredients = new[]
            {
                new { ingredientId = Rice, quantity = (decimal?)200, unitId = (Guid?)Gram, note = (string?)null },
                new { ingredientId = Salt, quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null },
                new { ingredientId = Flour, quantity = (decimal?)100, unitId = (Guid?)Gram, note = (string?)null },
            },
        });
        Assert.Equal(HttpStatusCode.Created, recipe.StatusCode);
        var recipeId = (await recipe.JsonAsync()).GetProperty("id").GetGuid();
        using var entry = await owner.Client.PostAsync(
            $"/api/households/{household}/meal-plans/{Monday:yyyy-MM-dd}/entries",
            new { date = Monday.ToString("yyyy-MM-dd"), recipeId, servings = 4 });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        return (owner, household);
    }

    private static async Task<string[]> CheckYouHaveAsync(ApiClient client, Guid household)
    {
        using var response = await client.PostAsync(ListUrl(household, "/generate"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.JsonAsync();
        return [.. list.GetProperty("checkYouHave").EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];
    }

    private static async Task<JsonElement> ListAsync(ApiClient client, Guid household)
    {
        using var response = await client.GetAsync(Url(household));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    [Fact]
    public async Task Confirmed_staples_are_no_longer_prompted_when_the_list_is_generated()
    {
        await using var host = Host();
        var (owner, household) = await ScenarioAsync(host);
        Assert.Equal(["Hvetemel", "Salt"], await CheckYouHaveAsync(owner.Client, household));

        using var confirm = await owner.Client.PutAsync(Url(household, $"/{Salt}"), new { });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var item = await confirm.JsonAsync();
        Assert.Equal("Salt", item.GetProperty("name").GetString());
        Assert.Equal(owner.User.Id, item.GetProperty("confirmedByUserId").GetGuid());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("expiresAt").ValueKind);

        Assert.Equal(["Hvetemel"], await CheckYouHaveAsync(owner.Client, household));

        // The main list is untouched and "you have" lists what was confirmed.
        using var generate = await owner.Client.PostAsync(ListUrl(household, "/generate"));
        Assert.Equal(1, (await generate.JsonAsync()).GetProperty("main").GetArrayLength());
        var pantry = await ListAsync(owner.Client, household);
        Assert.Equal(["Salt"], pantry.EnumerateArray().Select(p => p.GetProperty("name").GetString()!));
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Confirming_twice_keeps_one_row_and_the_latest_expiry_wins()
    {
        await using var host = Host();
        var (owner, household) = await ScenarioAsync(host);

        (await owner.Client.PutAsync(Url(household, $"/{Salt}"), new { })).Dispose();
        using var again = await owner.Client.PutAsync(Url(household, $"/{Salt}"), new { expiresAt = host.Time.Now.AddDays(30) });

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.NotEqual(JsonValueKind.Null, (await again.JsonAsync()).GetProperty("expiresAt").ValueKind);
        Assert.Equal(1, (await ListAsync(owner.Client, household)).GetArrayLength());
        Assert.Equal(1, (int)(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM PantryItems WHERE HouseholdId = @h", ("@h", household)))!);
    }

    [Fact]
    public async Task An_expired_confirmation_prompts_again_and_is_not_listed()
    {
        await using var host = Host();
        var (owner, household) = await ScenarioAsync(host);
        (await owner.Client.PutAsync(Url(household, $"/{Salt}"), new { expiresAt = host.Time.Now.AddDays(10) })).Dispose();
        Assert.Equal(["Hvetemel"], await CheckYouHaveAsync(owner.Client, household));

        host.Time.Now = host.Time.Now.AddDays(9);
        Assert.Equal(["Hvetemel"], await CheckYouHaveAsync(owner.Client, household));
        Assert.Equal(1, (await ListAsync(owner.Client, household)).GetArrayLength());

        host.Time.Now = host.Time.Now.AddDays(2);
        Assert.Equal(["Hvetemel", "Salt"], await CheckYouHaveAsync(owner.Client, household));
        Assert.Equal(0, (await ListAsync(owner.Client, household)).GetArrayLength());

        // Confirming again after expiry works and hides it again.
        (await owner.Client.PutAsync(Url(household, $"/{Salt}"), new { })).Dispose();
        Assert.Equal(["Hvetemel"], await CheckYouHaveAsync(owner.Client, household));
    }

    [Fact]
    public async Task Un_confirming_prompts_the_staple_again_and_is_idempotent()
    {
        await using var host = Host();
        var (owner, household) = await ScenarioAsync(host);
        (await owner.Client.PutAsync(Url(household, $"/{Salt}"), new { })).Dispose();
        Assert.Equal(["Hvetemel"], await CheckYouHaveAsync(owner.Client, household));

        using var delete = await owner.Client.DeleteAsync(Url(household, $"/{Salt}"));
        using var deleteAgain = await owner.Client.DeleteAsync(Url(household, $"/{Salt}"));

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleteAgain.StatusCode);
        Assert.Equal(["Hvetemel", "Salt"], await CheckYouHaveAsync(owner.Client, household));
        Assert.Equal(0, (await ListAsync(owner.Client, household)).GetArrayLength());
    }

    [Fact]
    public async Task Only_existing_ingredients_with_a_future_expiry_can_be_confirmed()
    {
        await using var host = Host();
        var (owner, household) = await ScenarioAsync(host);

        using var unknown = await owner.Client.PutAsync(Url(household, $"/{Guid.NewGuid()}"), new { });
        using var past = await owner.Client.PutAsync(Url(household, $"/{Salt}"), new { expiresAt = host.Time.Now.AddMinutes(-1) });

        await AssertValidationAsync(unknown, "ingredientId");
        await AssertValidationAsync(past, "expiresAt");
        Assert.Equal(0, (await ListAsync(owner.Client, household)).GetArrayLength());
    }

    [Fact]
    public async Task Every_member_including_a_child_may_confirm_and_un_confirm()
    {
        await using var host = Host();
        var (owner, household) = await ScenarioAsync(host);
        var child = await host.JoinAsync(owner, household, "Child");

        using var confirm = await child.Client.PutAsync(Url(household, $"/{Salt}"), new { });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal(child.User.Id, (await confirm.JsonAsync()).GetProperty("confirmedByUserId").GetGuid());
        Assert.Equal(1, (await ListAsync(owner.Client, household)).GetArrayLength());

        using var delete = await child.Client.DeleteAsync(Url(household, $"/{Salt}"));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Another_household_cannot_read_change_or_see_the_pantry()
    {
        await using var host = Host();
        var (owner, household) = await ScenarioAsync(host);
        (await owner.Client.PutAsync(Url(household, $"/{Salt}"), new { })).Dispose();
        var stranger = await host.SignInAsync();
        var strangerHousehold = await stranger.Client.CreateHouseholdAsync();

        using (var read = await stranger.Client.GetAsync(Url(household)))
        {
            await read.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using (var confirm = await stranger.Client.PutAsync(Url(household, $"/{Flour}"), new { }))
        {
            await confirm.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using (var delete = await stranger.Client.DeleteAsync(Url(household, $"/{Salt}")))
        {
            await delete.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        // Own household: nothing of the other pantry is visible, and un-confirming "the same" ingredient changes only your own.
        Assert.Equal(0, (await ListAsync(stranger.Client, strangerHousehold)).GetArrayLength());
        using (var ownDelete = await stranger.Client.DeleteAsync(Url(strangerHousehold, $"/{Salt}")))
        {
            Assert.Equal(HttpStatusCode.NoContent, ownDelete.StatusCode);
        }

        Assert.Equal(1, (await ListAsync(owner.Client, household)).GetArrayLength());

        // Same ingredient confirmed by the other household stays separate.
        (await stranger.Client.PutAsync(Url(strangerHousehold, $"/{Salt}"), new { })).Dispose();
        (await owner.Client.DeleteAsync(Url(household, $"/{Salt}"))).Dispose();
        Assert.Equal(1, (await ListAsync(stranger.Client, strangerHousehold)).GetArrayLength());
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Another_households_ingredient_id_is_refused_and_nothing_is_saved()
    {
        await using var host = Host();
        var (owner, household) = await ScenarioAsync(host);
        var stranger = await host.SignInAsync();
        var strangerHousehold = await stranger.Client.CreateHouseholdAsync();
        using var created = await stranger.Client.PostAsync(
            $"/api/households/{strangerHousehold}/ingredients", new { name = "Hemmelig salt " + Guid.NewGuid().ToString("N"), isStaple = true, dimension = "Mass" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var foreign = (await created.JsonAsync()).GetProperty("id").GetGuid();

        using var confirm = await owner.Client.PutAsync(Url(household, $"/{foreign}"), new { });

        await AssertValidationAsync(confirm, "ingredientId");
        Assert.Equal(0, (int)(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM PantryItems WHERE IngredientId = @i", ("@i", foreign)))!);
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task A_household_can_confirm_its_own_staple_ingredient()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        using var created = await owner.Client.PostAsync(
            $"/api/households/{household}/ingredients", new { name = "Husets krydder " + Guid.NewGuid().ToString("N"), isStaple = true, dimension = "Mass" });
        var own = (await created.JsonAsync()).GetProperty("id").GetGuid();

        using var confirm = await owner.Client.PutAsync(Url(household, $"/{own}"), new { });

        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        await AssertNoCrossHouseholdLinksAsync();
    }
}
