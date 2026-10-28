using System.Net;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.TenantIsolation;

namespace Foodplanner.Api.Tests.Catalog;

/// <summary>T-076 on T-079: recipe line alternatives are counted as usage, block a delete and move (or drop) on a merge.</summary>
public sealed class OwnIngredientAlternativeTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private AuthHost Host()
    {
        var host = HouseholdHost.Create(db.ConnectionString);
        host.DefaultLanguage = "nb";
        return host;
    }

    private static string Base(Guid household) => $"/api/households/{household}";

    private static async Task<Guid> OwnAsync(ApiClient client, Guid household)
    {
        using var response = await client.PostAsync($"{Base(household)}/ingredients", new { name = "Alt" + Guid.NewGuid().ToString("N")[..20], dimension = "Mass" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> RecipeAsync(ApiClient client, Guid household, Guid primary, params Guid[] alternatives)
    {
        using var response = await client.PostAsync($"{Base(household)}/recipes", new
        {
            title = "Alt " + Guid.NewGuid().ToString("N")[..8],
            servings = 4,
            ingredients = new[] { new { ingredientId = primary, quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null, amountKind = "AsNeeded", purpose = "Frying", isOptional = false, alternativeIds = alternatives } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid[]> AlternativesOfAsync(ApiClient client, Guid household, Guid recipe)
    {
        using var response = await client.GetAsync($"{Base(household)}/recipes/{recipe}");
        var line = (await response.JsonAsync()).GetProperty("ingredients")[0];
        return [.. line.GetProperty("alternatives").EnumerateArray().Select(a => a.GetProperty("ingredientId").GetGuid())];
    }

    [Fact]
    public async Task An_ingredient_used_only_as_an_alternative_counts_as_used_and_cannot_be_deleted()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var primary = await OwnAsync(owner.Client, household);
        var alternative = await OwnAsync(owner.Client, household);
        await RecipeAsync(owner.Client, household, primary, alternative);

        using var list = await owner.Client.GetAsync($"{Base(household)}/ingredients/own");
        var row = (await list.JsonAsync()).EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == alternative);
        Assert.Equal(1, row.GetProperty("usage").GetProperty("recipes").GetInt32());

        using var delete = await owner.Client.DeleteAsync($"{Base(household)}/ingredients/{alternative}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }

    [Fact]
    public async Task Merge_moves_alternatives_and_drops_the_ones_that_would_equal_the_line_or_repeat()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var source = await OwnAsync(owner.Client, household);
        var target = await OwnAsync(owner.Client, household);
        var other = await OwnAsync(owner.Client, household);

        var moves = await RecipeAsync(owner.Client, household, other, source);          // other (or source)  ->  other (or target)
        var equalsLine = await RecipeAsync(owner.Client, household, target, source);    // target (or source) ->  target (alternative dropped)
        var repeats = await RecipeAsync(owner.Client, household, other, source, target); // other (or source, target) -> other (or target)
        var primaryMoves = await RecipeAsync(owner.Client, household, source, other);   // source (or other)  ->  target (or other)
        var primaryWithTarget = await RecipeAsync(owner.Client, household, source, target, other); // source (or target, other) -> target (or other)

        using var merged = await owner.Client.PostAsync($"{Base(household)}/ingredients/{source}/merge", new { targetIngredientId = target });
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        Assert.Equal(5, (await merged.JsonAsync()).GetProperty("moved").GetProperty("recipes").GetInt32());

        Assert.Equal([target], await AlternativesOfAsync(owner.Client, household, moves));
        Assert.Empty(await AlternativesOfAsync(owner.Client, household, equalsLine));
        Assert.Equal([target], await AlternativesOfAsync(owner.Client, household, repeats));
        Assert.Equal([other], await AlternativesOfAsync(owner.Client, household, primaryMoves));
        Assert.Equal([other], await AlternativesOfAsync(owner.Client, household, primaryWithTarget)); // never "target or target"
        using var gone = await owner.Client.GetAsync($"{Base(household)}/ingredients/own");
        Assert.DoesNotContain((await gone.JsonAsync()).EnumerateArray(), i => i.GetProperty("id").GetGuid() == source);

        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }
}
