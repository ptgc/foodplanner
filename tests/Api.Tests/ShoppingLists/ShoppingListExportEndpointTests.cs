using System.Net;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Domain.Catalog;

namespace Foodplanner.Api.Tests.ShoppingLists;

public sealed class ShoppingListExportEndpointTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly Guid Rice = IngredientSeed.All.Single(r => r.Key == "ris").Id;
    private static readonly Guid Egg = IngredientSeed.All.Single(r => r.Key == "egg").Id;
    private static readonly Guid Salt = IngredientSeed.All.Single(r => r.Key == "salt").Id; // staple
    private static readonly Guid Gram = UnitSeed.All.Single(r => r.Code == "g").Id;
    private static readonly Guid Pieces = UnitSeed.All.Single(r => r.Code == "stk").Id;

    private static string Url(Guid household, string query = "", string week = "2026-10-26") =>
        $"/api/households/{household}/shopping-lists/{week}/export{query}";

    private async Task<(SignedInUser Owner, Guid Household, Guid EggItem)> ScenarioAsync(AuthHost host)
    {
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        using var recipe = await owner.Client.PostAsync($"/api/households/{household}/recipes", new
        {
            title = "Rice and egg",
            servings = 4,
            ingredients = new[]
            {
                new { ingredientId = Rice, quantity = (decimal?)500, unitId = (Guid?)Gram, note = (string?)null },
                new { ingredientId = Egg, quantity = (decimal?)5, unitId = (Guid?)Pieces, note = (string?)null },
                new { ingredientId = Salt, quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null },
            },
        });
        Assert.Equal(HttpStatusCode.Created, recipe.StatusCode);
        var recipeId = (await recipe.JsonAsync()).GetProperty("id").GetGuid();
        using var entry = await owner.Client.PostAsync(
            $"/api/households/{household}/meal-plans/{Monday:yyyy-MM-dd}/entries",
            new { date = Monday.ToString("yyyy-MM-dd"), recipeId, servings = 4 });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);

        using var generated = await owner.Client.PostAsync($"/api/households/{household}/shopping-lists/{Monday:yyyy-MM-dd}/generate");
        var list = await generated.JsonAsync();
        var egg = list.GetProperty("main").EnumerateArray().Single(i => i.GetProperty("ingredientId").GetGuid() == Egg).GetProperty("id").GetGuid();

        using var manual = await owner.Client.PostAsync(
            $"/api/households/{household}/shopping-lists/{Monday:yyyy-MM-dd}/items", new { label = "Kaffefilter" });
        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);
        return (owner, household, egg);
    }

    [Fact]
    public async Task Exports_the_list_as_text_in_the_request_language()
    {
        await using var host = HouseholdHost.Create(db.ConnectionString);
        var (owner, household, _) = await ScenarioAsync(host);

        owner.Client.Language = "nb";
        using var nb = await owner.Client.GetAsync(Url(household, "?format=text"));
        Assert.Equal(HttpStatusCode.OK, nb.StatusCode);
        var nbBody = await nb.JsonAsync();
        Assert.Equal("text", nbBody.GetProperty("format").GetString());
        Assert.Equal("Handleliste, uke 44", nbBody.GetProperty("title").GetString());
        Assert.Equal(
            "Å handle\n- Egg 5 stk\n- Ris 500 g\n- Kaffefilter\n\nSjekk at du har\n- Salt",
            nbBody.GetProperty("text").GetString());

        owner.Client.Language = "en";
        using var en = await owner.Client.GetAsync(Url(household));
        var enBody = await en.JsonAsync();
        Assert.Equal("Shopping list, week 44", enBody.GetProperty("title").GetString());
        Assert.Equal(
            "To buy\n- Egg 5 stk\n- Rice 500 g\n- Kaffefilter\n\nCheck you have\n- Salt",
            enBody.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Checked_items_are_not_in_the_text()
    {
        await using var host = HouseholdHost.Create(db.ConnectionString);
        var (owner, household, egg) = await ScenarioAsync(host);
        using var check = await owner.Client.PutAsync(
            $"/api/households/{household}/shopping-lists/{Monday:yyyy-MM-dd}/items/{egg}/check", new { @checked = true });
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);

        owner.Client.Language = "en";
        using var response = await owner.Client.GetAsync(Url(household));
        var text = (await response.JsonAsync()).GetProperty("text").GetString()!;

        Assert.DoesNotContain("Egg", text, StringComparison.Ordinal);
        Assert.Contains("- Rice 500 g", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_week_without_a_list_exports_the_empty_sentence()
    {
        await using var host = HouseholdHost.Create(db.ConnectionString);
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var response = await owner.Client.GetAsync(Url(household));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Nothing to buy.", (await response.JsonAsync()).GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_non_monday_or_unknown_format_is_a_validation_problem()
    {
        await using var host = HouseholdHost.Create(db.ConnectionString);
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var tuesday = await owner.Client.GetAsync(Url(household, week: "2026-10-27"));
        Assert.Equal(HttpStatusCode.BadRequest, tuesday.StatusCode);
        Assert.True((await tuesday.JsonAsync()).GetProperty("errors").TryGetProperty("weekStart", out _));

        using var format = await owner.Client.GetAsync(Url(household, "?format=oda"));
        Assert.Equal(HttpStatusCode.BadRequest, format.StatusCode);
        Assert.True((await format.JsonAsync()).GetProperty("errors").TryGetProperty("format", out _));
    }

    [Fact]
    public async Task A_non_member_cannot_export_another_households_list()
    {
        await using var host = HouseholdHost.Create(db.ConnectionString);
        var (_, household, _) = await ScenarioAsync(host);
        var stranger = await host.SignInAsync();
        await stranger.Client.CreateHouseholdAsync();

        using var response = await stranger.Client.GetAsync(Url(household));

        await response.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }
}
