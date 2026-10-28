using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Api.Tests.Recipes;

/// <summary>T-076: amount kind, purpose, optional and alternatives on recipe lines, through the API.</summary>
public sealed class RecipeAmountKindTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly Guid Butter = Ingredient("smor");
    private static readonly Guid Oil = Ingredient("rapsolje");
    private static readonly Guid Salt = Ingredient("salt");
    private static readonly Guid Pepper = Ingredient("sort_pepper");
    private static readonly Guid Cheese = Ingredient("ost");
    private static readonly Guid Gram = UnitSeed.All.Single(r => r.Code == "g").Id;
    private static readonly Guid Tbsp = UnitSeed.All.Single(r => r.Code == "ss").Id;

    private static Guid Ingredient(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    private AuthHost Host()
    {
        var host = HouseholdHost.Create(db.ConnectionString);
        host.DefaultLanguage = "nb";
        return host;
    }

    private static string Url(Guid household, string rest = "") => $"/api/households/{household}/recipes{rest}";

    private async Task<int> CountAsync(string sql, params (string Name, object Value)[] parameters) =>
        (int)(await HouseholdScenario.ScalarAsync(db.ConnectionString, sql, parameters))!;

    private static object Body(object[] ingredients) => new { title = "Stek " + Guid.NewGuid().ToString("N"), servings = 4, ingredients };

    private static object FryingFat(Guid[]? alternatives = null) => new
    {
        ingredientId = Butter,
        quantity = (decimal?)null,
        unitId = (Guid?)null,
        note = (string?)null,
        amountKind = "AsNeeded",
        purpose = "Frying",
        isOptional = false,
        alternativeIds = alternatives ?? [Oil],
    };

    private static async Task AssertValidationAsync(HttpResponseMessage response, params string[] keys)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var errors = (await response.JsonAsync()).GetProperty("errors");
        foreach (var key in keys)
        {
            Assert.True(errors.TryGetProperty(key, out _), $"expected an error for '{key}', got {errors}");
        }
    }

    [Fact]
    public async Task Kind_purpose_optional_and_alternatives_are_saved_returned_and_replaced_on_update()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var created = await owner.Client.PostAsync(Url(household), Body(
        [
            FryingFat([Oil, Cheese]),
            new { ingredientId = Salt, quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null, amountKind = "ToTaste", purpose = "Seasoning", isOptional = false, alternativeIds = (Guid[]?)null },
            new { ingredientId = Cheese, quantity = (decimal?)1m, unitId = (Guid?)Gram, note = (string?)null, amountKind = "Measured", purpose = "Garnish", isOptional = true, alternativeIds = (Guid[]?)null },
        ]));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.JsonAsync();
        var id = body.GetProperty("id").GetGuid();
        var lines = body.GetProperty("ingredients").EnumerateArray().ToList();
        Assert.Equal("AsNeeded", lines[0].GetProperty("amountKind").GetString());
        Assert.Equal("Frying", lines[0].GetProperty("purpose").GetString());
        Assert.Equal(["Rapsolje", "Ost"], lines[0].GetProperty("alternatives").EnumerateArray().Select(a => a.GetProperty("ingredientName").GetString()));
        Assert.Equal("ToTaste", lines[1].GetProperty("amountKind").GetString());
        Assert.Equal("Seasoning", lines[1].GetProperty("purpose").GetString());
        Assert.Equal("Measured", lines[2].GetProperty("amountKind").GetString());
        Assert.True(lines[2].GetProperty("isOptional").GetBoolean());
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM RecipeIngredientAlternatives a JOIN RecipeIngredients l ON l.Id = a.RecipeIngredientId WHERE l.RecipeId = @r", ("@r", id)));

        // The English names come in English.
        owner.Client.Language = "en";
        using var english = await owner.Client.GetAsync(Url(household, "/" + id));
        var first = (await english.JsonAsync()).GetProperty("ingredients")[0];
        Assert.Equal("Rapeseed oil", first.GetProperty("alternatives")[0].GetProperty("ingredientName").GetString());
        owner.Client.Language = "nb";

        // PUT replaces the alternatives of the lines (the old ones go with the old lines).
        using var updated = await owner.Client.PutAsync(Url(household, "/" + id), Body([FryingFat([Oil])]));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM RecipeIngredientAlternatives a JOIN RecipeIngredients l ON l.Id = a.RecipeIngredientId WHERE l.RecipeId = @r", ("@r", id)));

        // Repeated ids are ignored.
        using var repeated = await owner.Client.PutAsync(Url(household, "/" + id), Body([FryingFat([Oil, Oil])]));
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM RecipeIngredientAlternatives a JOIN RecipeIngredients l ON l.Id = a.RecipeIngredientId WHERE l.RecipeId = @r", ("@r", id)));

        // Deleting the recipe removes lines and alternatives.
        using var deleted = await owner.Client.DeleteAsync(Url(household, "/" + id));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM RecipeIngredientAlternatives WHERE HouseholdId = @h", ("@h", household)));
    }

    [Fact]
    public async Task A_line_without_a_kind_gets_it_from_the_quantity()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var created = await owner.Client.PostAsync(Url(household), Body(
        [
            new { ingredientId = Butter, quantity = (decimal?)2m, unitId = (Guid?)Tbsp, note = (string?)null },
            new { ingredientId = Salt, quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null },
        ]));

        var lines = (await created.JsonAsync()).GetProperty("ingredients").EnumerateArray().ToList();
        Assert.Equal("Measured", lines[0].GetProperty("amountKind").GetString());
        Assert.Equal("ToTaste", lines[1].GetProperty("amountKind").GetString());
        Assert.Equal("Main", lines[1].GetProperty("purpose").GetString());
        Assert.False(lines[1].GetProperty("isOptional").GetBoolean());
        Assert.Empty(lines[1].GetProperty("alternatives").EnumerateArray());
    }

    [Fact]
    public async Task Invalid_combinations_are_validation_problems_and_nothing_is_saved()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        object Line(string? kind, decimal? quantity, Guid? unit, string? purpose = null, Guid[]? alternatives = null, Guid? ingredient = null) =>
            new { ingredientId = ingredient ?? Butter, quantity, unitId = unit, note = (string?)null, amountKind = kind, purpose, alternativeIds = alternatives };

        var cases = new (string[] Keys, object Line)[]
        {
            (["ingredients[0].amountKind"], Line("Sometimes", null, null)),
            (["ingredients[0].purpose"], Line("ToTaste", null, null, "Dessert")),
            (["ingredients[0].quantity"], Line("Measured", null, null)),
            (["ingredients[0].unitId"], Line("Measured", 1m, null)),
            (["ingredients[0].quantity"], Line("ToTaste", 1m, Gram)),
            (["ingredients[0].unitId"], Line("ToTaste", null, Gram)),
            (["ingredients[0].quantity"], Line("AsNeeded", 2m, null)),
            (["ingredients[0].unitId"], Line("AsNeeded", null, Gram)),
            (["ingredients[0].alternativeIds"], Line("AsNeeded", null, null, null, [Oil, Salt, Pepper, Cheese, Gram, Tbsp])),
            (["ingredients[0].alternativeIds[0]"], Line("AsNeeded", null, null, null, [Butter])),
            (["ingredients[0].alternativeIds[0]"], Line("AsNeeded", null, null, null, [Guid.Empty])),
            (["ingredients[0].alternativeIds[0]"], Line("AsNeeded", null, null, null, [Guid.NewGuid()])),
        };
        foreach (var (keys, line) in cases)
        {
            using var response = await owner.Client.PostAsync(Url(household), Body([line]));
            await AssertValidationAsync(response, keys);
        }

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Recipes WHERE HouseholdId = @h", ("@h", household)));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM RecipeIngredientAlternatives WHERE HouseholdId = @h", ("@h", household)));
    }

    [Fact]
    public async Task An_alternative_of_another_household_is_refused_like_an_unknown_one_and_an_own_one_is_accepted()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        var householdB = await b.Client.CreateHouseholdAsync();
        using var secret = await b.Client.PostAsync($"/api/households/{householdB}/ingredients", new { name = "Hemmelig " + Guid.NewGuid().ToString("N"), dimension = "Mass" });
        var foreign = (await secret.JsonAsync()).GetProperty("id").GetGuid();
        using var mine = await a.Client.PostAsync($"/api/households/{householdA}/ingredients", new { name = "Margarin " + Guid.NewGuid().ToString("N"), dimension = "Mass" });
        var own = (await mine.JsonAsync()).GetProperty("id").GetGuid();

        using var refused = await a.Client.PostAsync(Url(householdA), Body([FryingFat([foreign])]));
        await AssertValidationAsync(refused, "ingredients[0].alternativeIds[0]");
        using var unknown = await a.Client.PostAsync(Url(householdA), Body([FryingFat([Guid.NewGuid()])]));
        await AssertValidationAsync(unknown, "ingredients[0].alternativeIds[0]");
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM RecipeIngredientAlternatives WHERE IngredientId = @i", ("@i", foreign)));

        using var accepted = await a.Client.PostAsync(Url(householdA), Body([FryingFat([own, Oil])]));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM RecipeIngredientAlternatives WHERE HouseholdId = @h", ("@h", householdA)));

        // Household B cannot see household A's recipe, so not its alternatives either.
        var id = (await accepted.JsonAsync()).GetProperty("id").GetGuid();
        using var other = await b.Client.GetAsync(Url(householdB, "/" + id));
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);

        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }

    [Fact]
    public async Task Scaling_never_changes_a_to_taste_or_as_needed_line()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        using var created = await owner.Client.PostAsync(Url(household), Body(
        [
            new { ingredientId = Cheese, quantity = (decimal?)100m, unitId = (Guid?)Gram, note = (string?)null, amountKind = "Measured", purpose = "Main", isOptional = false, alternativeIds = (Guid[]?)null },
            FryingFat(),
            new { ingredientId = Salt, quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null, amountKind = "ToTaste", purpose = "Seasoning", isOptional = false, alternativeIds = (Guid[]?)null },
        ]));
        var id = (await created.JsonAsync()).GetProperty("id").GetGuid();

        using var scaled = await owner.Client.GetAsync(Url(household, $"/{id}/scaled?servings=12"));

        Assert.Equal(HttpStatusCode.OK, scaled.StatusCode);
        var lines = (await scaled.JsonAsync()).GetProperty("ingredients").EnumerateArray().ToList();
        Assert.Equal(300m, lines[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("quantity").ValueKind);
        Assert.Equal("AsNeeded", lines[1].GetProperty("amountKind").GetString());
        Assert.Equal("Rapsolje", lines[1].GetProperty("alternatives")[0].GetProperty("ingredientName").GetString());
        Assert.Equal(JsonValueKind.Null, lines[2].GetProperty("quantity").ValueKind);
    }
}
