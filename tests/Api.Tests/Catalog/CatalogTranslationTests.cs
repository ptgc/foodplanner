using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Recipes;
using Microsoft.Data.SqlClient;

namespace Foodplanner.Api.Tests.Catalog;

/// <summary>Translatable catalog data (T-039): the language of a request, the fallbacks, search, duplicates, and household text.</summary>
public sealed class CatalogTranslationTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly Guid Onion = IngredientSeed.All.Single(r => r.Key == "loek").Id;
    private static readonly Guid Flour = IngredientSeed.All.Single(r => r.Key == "hvetemel").Id;
    private static readonly Guid Rice = IngredientSeed.All.Single(r => r.Key == "ris").Id;
    private static readonly Guid Tablespoon = UnitSeed.All.Single(r => r.Code == "ss").Id;
    private static readonly Guid Vegetarian = TagSeed.All.Single(t => t.Key == "vegetar").Id;
    private static readonly Guid Dinner = TagSeed.All.Single(t => t.Key == "middag").Id;

    private AuthHost Host() => HouseholdHost.Create(db.ConnectionString);

    private async Task<(AuthHost Host, SignedInUser Owner, Guid Household)> StartAsync()
    {
        var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        return (host, owner, household);
    }

    private static async Task<List<JsonElement>> GetListAsync(ApiClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.JsonAsync()).EnumerateArray()];
    }

    private static string? NameOf(IEnumerable<JsonElement> rows, Guid id) =>
        rows.Single(r => r.GetProperty("id").GetGuid() == id).GetProperty("name").GetString();

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("nb", "Løk")]
    [InlineData("nb-NO,nb;q=0.9,en;q=0.8", "Løk")]
    [InlineData("no", "Løk")]
    [InlineData("nn", "Løk")]
    [InlineData("en", "Onion")]
    [InlineData("en-GB,en;q=0.9", "Onion")]
    [InlineData("fr-FR,fr;q=0.9", "Onion")]
    [InlineData("en;q=0.5,nb;q=0.9", "Løk")]
    [InlineData(null, "Onion")]
    public async Task Ingredient_names_follow_Accept_Language(string? header, string expected)
    {
        var (host, owner, household) = await StartAsync();
        await using var _ = host;
        owner.Client.Language = header;

        var rows = await GetListAsync(owner.Client, $"/api/households/{household}/ingredients?limit=100");

        Assert.Equal(expected, NameOf(rows, Onion));
    }

    [Fact]
    public async Task Units_have_a_stable_code_and_a_localized_name()
    {
        var (host, owner, _) = await StartAsync();
        await using var _ = host;

        owner.Client.Language = "nb";
        var nb = await GetListAsync(owner.Client, "/api/units");
        owner.Client.Language = "en";
        var en = await GetListAsync(owner.Client, "/api/units");

        Assert.Equal(UnitSeed.All.Count, nb.Count);
        Assert.Equal("spiseskje", NameOf(nb, Tablespoon));
        Assert.Equal("tablespoon", NameOf(en, Tablespoon));
        Assert.Equal(nb.Select(u => u.GetProperty("code").GetString()), en.Select(u => u.GetProperty("code").GetString()));
        Assert.Contains(en, u => u.GetProperty("code").GetString() == "ss");
    }

    [Fact]
    public async Task Tags_are_localized_and_sorted_by_the_requested_language()
    {
        var (host, owner, household) = await StartAsync();
        await using var _ = host;
        var url = $"/api/households/{household}/tags";

        owner.Client.Language = "nb";
        var nb = await GetListAsync(owner.Client, url);
        owner.Client.Language = "en";
        var en = await GetListAsync(owner.Client, url);

        Assert.Equal("Vegetar", NameOf(nb, Vegetarian));
        Assert.Equal("Vegetarian", NameOf(en, Vegetarian));
        Assert.Equal("Middag", NameOf(nb, Dinner));
        Assert.Equal("Dinner", NameOf(en, Dinner));
        // Categories first, then tags, each by name in that language.
        var enCategories = en.Where(t => t.GetProperty("kind").GetString() == "Category").Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Baking", "Breakfast", "Dessert", "Dinner", "Lunch"], enCategories);
        var nbCategories = nb.Where(t => t.GetProperty("kind").GetString() == "Category").Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Bakst", "Dessert", "Frokost", "Lunsj", "Middag"], nbCategories);
        Assert.Equal(en.Select(t => t.GetProperty("id").GetGuid()).Count(), nb.Count);
    }

    [Fact]
    public async Task Search_matches_the_requested_language_only()
    {
        var (host, owner, household) = await StartAsync();
        await using var _ = host;
        var url = $"/api/households/{household}/ingredients?search=";

        owner.Client.Language = "nb";
        Assert.Contains(await GetListAsync(owner.Client, url + "hvitl"), r => r.GetProperty("id").GetGuid() == IngredientSeed.All.Single(i => i.Key == "hvitloek").Id);
        Assert.Empty(await GetListAsync(owner.Client, url + "garlic"));
        Assert.Contains(await GetListAsync(owner.Client, url + "HVETEM"), r => r.GetProperty("id").GetGuid() == Flour);

        owner.Client.Language = "en";
        var garlic = await GetListAsync(owner.Client, url + "GARLIC");
        Assert.Equal("Garlic", Assert.Single(garlic).GetProperty("name").GetString());
        Assert.Empty(await GetListAsync(owner.Client, url + "hvitl"));
        Assert.Contains(await GetListAsync(owner.Client, url + "flour"), r => r.GetProperty("id").GetGuid() == Flour);
    }

    [Fact]
    public async Task Ingredients_sort_by_the_language_collation_and_paging_is_stable()
    {
        var (host, owner, household) = await StartAsync();
        await using var _ = host;
        var url = $"/api/households/{household}/ingredients";

        owner.Client.Language = "nb";
        var all = await GetListAsync(owner.Client, url + "?limit=100");
        var names = all.Select(r => r.GetProperty("name").GetString()!).ToList();

        // Norwegian collation: the letters æ, ø, å come after z, in that order.
        Assert.True(names.IndexOf("Blomkål") < names.IndexOf("Blåbær"), "å sorts after o in Norwegian (Blomkål, Blåbær)");
        var onions = (await GetListAsync(owner.Client, url + "?search=l%C3%B8k")).Select(r => r.GetProperty("name").GetString());
        Assert.Equal(["Gressløk", "Hvitløk", "Løk", "Rødløk", "Vårløk"], onions);
        Assert.Equal(all.Select(r => r.GetProperty("id").GetGuid()), (await GetListAsync(owner.Client, url + "?limit=100")).Select(r => r.GetProperty("id").GetGuid()));

        owner.Client.Language = "en";
        var en = (await GetListAsync(owner.Client, url + "?limit=100")).Select(r => r.GetProperty("name").GetString()!).ToList();
        Assert.Equal(en.Order(StringComparer.OrdinalIgnoreCase).Take(10), en.Take(10));
        Assert.Equal("Apple", en[0]);
    }

    [Fact]
    public async Task Duplicate_checks_cover_the_english_and_the_norwegian_names_of_global_rows()
    {
        var (host, owner, household) = await StartAsync();
        await using var _ = host;
        var ingredients = $"/api/households/{household}/ingredients";
        var tags = $"/api/households/{household}/tags";

        foreach (var name in new[] { "Onion", "ONION", "løk", "Løk", "  Red   onion " })
        {
            using var response = await owner.Client.PostAsync(ingredients, new { name, dimension = "Count" });
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "ingredient-exists");
        }

        foreach (var name in new[] { "Vegetarian", "vegetar", "VEGETAR", "Kid friendly", "barnevennlig" })
        {
            using var response = await owner.Client.PostAsync(tags, new { name, kind = "Tag" });
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "tag-exists");
        }

        // Control: a name that is nobody's still works.
        using var created = await owner.Client.PostAsync(ingredients, new { name = $"Sourdough {Guid.NewGuid():N}"[..30], dimension = "Mass" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Household_entered_names_are_returned_as_typed_in_every_language()
    {
        var (host, owner, household) = await StartAsync();
        await using var _ = host;
        var name = $"Bestemors saus {Guid.NewGuid():N}"[..30];
        using var ingredient = await owner.Client.PostAsync($"/api/households/{household}/ingredients", new { name, dimension = "Volume" });
        Assert.Equal(HttpStatusCode.Created, ingredient.StatusCode);
        var ingredientId = (await ingredient.JsonAsync()).GetProperty("id").GetGuid();
        var tagName = $"Onsdagsmiddag {Guid.NewGuid():N}"[..30];
        using var tag = await owner.Client.PostAsync($"/api/households/{household}/tags", new { name = tagName, kind = "Tag" });
        var tagId = (await tag.JsonAsync()).GetProperty("id").GetGuid();

        foreach (var language in new[] { "nb", "en", null })
        {
            owner.Client.Language = language;
            var rows = await GetListAsync(owner.Client, $"/api/households/{household}/ingredients?search=Bestemors");
            var own = Assert.Single(rows, r => r.GetProperty("id").GetGuid() == ingredientId);
            Assert.Equal(name, own.GetProperty("name").GetString());
            Assert.False(own.GetProperty("isGlobal").GetBoolean());
            Assert.Equal(tagName, NameOf(await GetListAsync(owner.Client, $"/api/households/{household}/tags"), tagId));
        }
    }

    [Fact]
    public async Task Recipe_detail_and_list_use_the_request_language_for_ingredients_units_and_tags()
    {
        var (host, owner, household) = await StartAsync();
        await using var _ = host;
        owner.Client.Language = "en";
        using var created = await owner.Client.PostAsync($"/api/households/{household}/recipes", new
        {
            title = "Mormors lapskaus",
            servings = 4,
            ingredients = new[] { new { ingredientId = Flour, quantity = 2m, unitId = Tablespoon, note = (string?)null } },
            tagIds = new[] { Vegetarian, Dinner },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.JsonAsync()).GetProperty("id").GetGuid();

        foreach (var (language, flour, unit, tags) in new[]
        {
            ("en", "Wheat flour", "tablespoon", new[] { "Dinner", "Vegetarian" }),
            ("nb", "Hvetemel", "spiseskje", new[] { "Middag", "Vegetar" }),
        })
        {
            owner.Client.Language = language;
            using var detail = await owner.Client.GetAsync($"/api/households/{household}/recipes/{id}");
            var body = await detail.JsonAsync();
            var line = body.GetProperty("ingredients")[0];
            Assert.Equal(flour, line.GetProperty("ingredientName").GetString());
            Assert.Equal("ss", line.GetProperty("unitCode").GetString());
            Assert.Equal(unit, line.GetProperty("unitName").GetString());
            Assert.Equal(tags, body.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
            Assert.Equal("Mormors lapskaus", body.GetProperty("title").GetString()); // household text: as typed

            using var list = await owner.Client.GetAsync($"/api/households/{household}/recipes?search=lapskaus");
            var summary = (await list.JsonAsync()).GetProperty("items")[0];
            Assert.Equal(tags, summary.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        }
    }

    [Fact]
    public async Task Generated_shopping_list_items_are_stored_by_id_and_relabelled_when_the_language_changes()
    {
        var (host, owner, household) = await StartAsync();
        await using var _ = host;
        var monday = new DateOnly(2026, 11, 2);
        using var recipe = await owner.Client.PostAsync($"/api/households/{household}/recipes", new
        {
            title = "Risotto",
            servings = 2,
            ingredients = new[] { new { ingredientId = Rice, quantity = 200m, unitId = UnitSeed.All.Single(u => u.Code == "g").Id, note = (string?)null } },
        });
        var recipeId = (await recipe.JsonAsync()).GetProperty("id").GetGuid();
        using var plan = await owner.Client.PostAsync(
            $"/api/households/{household}/meal-plans/{monday:yyyy-MM-dd}/entries", new { date = monday.ToString("yyyy-MM-dd"), recipeId, servings = 2 });
        Assert.Equal(HttpStatusCode.Created, plan.StatusCode);
        using var manual = await owner.Client.PostAsync($"/api/households/{household}/shopping-lists/{monday:yyyy-MM-dd}/items", new { label = "Stearinlys" });
        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);

        owner.Client.Language = "en";
        using var generated = await owner.Client.PostAsync($"/api/households/{household}/shopping-lists/{monday:yyyy-MM-dd}/generate");
        var en = await generated.JsonAsync();
        owner.Client.Language = "nb";
        using var read = await owner.Client.GetAsync($"/api/households/{household}/shopping-lists/{monday:yyyy-MM-dd}");
        var nb = await read.JsonAsync();

        string[] Names(JsonElement list) => [.. list.GetProperty("main").EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];
        Assert.Equal(["Rice", "Stearinlys"], Names(en));
        Assert.Equal(["Ris", "Stearinlys"], Names(nb));
        var riceItem = nb.GetProperty("main").EnumerateArray().Single(i => i.GetProperty("ingredientId").ValueKind == JsonValueKind.String);
        Assert.Equal(Rice, riceItem.GetProperty("ingredientId").GetGuid());
        var quantity = riceItem.GetProperty("quantities")[0];
        Assert.Equal("g", quantity.GetProperty("unitCode").GetString());
        Assert.Equal("gram", quantity.GetProperty("unitName").GetString());
        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM ShoppingListItems WHERE IngredientId = '{Rice}'")); // an id, never a stored name
    }

    [Fact]
    public async Task A_missing_nb_name_falls_back_to_en_and_a_missing_en_name_to_the_row_key()
    {
        var (host, owner, household) = await StartAsync();
        await using var _ = host;
        var cardamom = IngredientSeed.All.Single(i => i.Key == "kardemomme").Id;
        var url = $"/api/households/{household}/ingredients?limit=100";
        try
        {
            await ExecuteAsync($"DELETE FROM CatalogTranslations WHERE Kind = 'ingredient' AND EntityId = '{cardamom}' AND Locale = 'nb'");
            owner.Client.Language = "nb";
            Assert.Equal("Cardamom", NameOf(await GetListAsync(owner.Client, url), cardamom));

            await ExecuteAsync($"DELETE FROM CatalogTranslations WHERE Kind = 'ingredient' AND EntityId = '{cardamom}'");
            await ExecuteAsync($"UPDATE Ingredients SET Name = 'cardamom-key' WHERE Id = '{cardamom}'");
            Assert.Equal("cardamom-key", NameOf(await GetListAsync(owner.Client, url), cardamom));
            owner.Client.Language = "en";
            Assert.Equal("cardamom-key", NameOf(await GetListAsync(owner.Client, url), cardamom));
        }
        finally
        {
            await ExecuteAsync($"UPDATE Ingredients SET Name = 'Cardamom' WHERE Id = '{cardamom}'");
            await ExecuteAsync($"DELETE FROM CatalogTranslations WHERE Kind = 'ingredient' AND EntityId = '{cardamom}'");
            await ExecuteAsync($"INSERT INTO CatalogTranslations (Kind, EntityId, Locale, Name) VALUES ('ingredient', '{cardamom}', 'en', 'Cardamom'), ('ingredient', '{cardamom}', 'nb', 'Kardemomme')");
        }
    }

    [Fact]
    public async Task Seed_has_translations_for_every_row_and_locale_in_the_database()
    {
        var expected = (IngredientSeed.All.Count + UnitSeed.All.Count + TagSeed.All.Count) * 2;
        Assert.Equal(expected, await ScalarAsync("SELECT COUNT(*) FROM CatalogTranslations"));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM Ingredients WHERE HouseholdId IS NULL AND Id NOT IN (SELECT EntityId FROM CatalogTranslations WHERE Kind = 'ingredient' AND Locale = 'nb')"));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM Ingredients i JOIN CatalogTranslations t ON t.EntityId = i.Id AND t.Locale = 'en' WHERE i.Name <> t.Name"));
    }

    private async Task<int> ScalarAsync(string sql) => (int)(await HouseholdScenario.ScalarAsync(db.ConnectionString, sql))!;
}
