using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Api.Tests.Recipes;

/// <summary>Tags, categories and the recipe list filters (T-023).</summary>
public sealed class RecipeTagAndFilterTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly Guid Salt = IngredientSeed.All.Single(r => r.Key == "salt").Id;
    private static readonly Guid Flour = IngredientSeed.All.Single(r => r.Key == "hvetemel").Id;
    private static readonly Guid Gram = UnitSeed.All.Single(r => r.Code == "g").Id;
    private static readonly Guid Vegetar = TagSeed.All.Single(t => t.Key == "vegetar").Id;
    private static readonly Guid Rask = TagSeed.All.Single(t => t.Key == "rask").Id;
    private static readonly Guid Middag = TagSeed.All.Single(t => t.Key == "middag").Id;

    private AuthHost Host() => HouseholdHost.Create(db.ConnectionString);

    private static string Url(Guid household, string rest = "") => $"/api/households/{household}/recipes{rest}";

    private static string TagUrl(Guid household, string rest = "") => $"/api/households/{household}/tags{rest}";

    private static string Name(string prefix) => $"{prefix} {Guid.NewGuid():N}";

    private async Task<int> CountAsync(string table, string where, params (string Name, object Value)[] parameters) =>
        (int)(await HouseholdScenario.ScalarAsync(db.ConnectionString, $"SELECT COUNT(*) FROM {table} WHERE {where}", parameters))!;

    private async Task AssertNoCrossHouseholdLinksAsync()
    {
        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }

    private static async Task<Guid> CreateTagAsync(ApiClient client, Guid household, string name, string kind = "Tag")
    {
        using var response = await client.PostAsync(TagUrl(household), new { name, kind });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateRecipeAsync(
        ApiClient client, Guid household, string title, Guid[]? tags = null, int? prep = null, int? cook = null, params Guid[] ingredients)
    {
        using var response = await client.PostAsync(Url(household), new
        {
            title,
            servings = 2,
            prepMinutes = prep,
            cookMinutes = cook,
            tagIds = tags ?? [],
            ingredients = ingredients.Select(i => new { ingredientId = i, quantity = 1m, unitId = Gram, note = (string?)null }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<(int Total, List<Guid> Ids)> ListAsync(ApiClient client, Guid household, string query)
    {
        using var response = await client.GetAsync(Url(household, query.Length == 0 ? "" : "?" + query));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        return (body.GetProperty("total").GetInt32(), body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList());
    }

    [Fact]
    public async Task Tags_list_shows_global_and_own_but_never_another_households()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        var householdB = await b.Client.CreateHouseholdAsync();
        var ownA = Name("Ost");
        var ownB = Name("Hemmelig");
        var idA = await CreateTagAsync(a.Client, householdA, ownA);
        await CreateTagAsync(b.Client, householdB, ownB, "Category");

        using var response = await a.Client.GetAsync(TagUrl(householdA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tags = (await response.JsonAsync()).EnumerateArray().ToList();
        Assert.Equal(TagSeed.All.Count + 1, tags.Count);
        Assert.Contains(tags, t => t.GetProperty("id").GetGuid() == idA && !t.GetProperty("isGlobal").GetBoolean());
        Assert.DoesNotContain(tags, t => t.GetProperty("name").GetString() == ownB);
        var global = tags.Where(t => t.GetProperty("isGlobal").GetBoolean()).ToList();
        Assert.Equal(TagSeed.All.Count, global.Count);
        Assert.Contains(global, t => t.GetProperty("name").GetString() == "Barnevennlig" && t.GetProperty("kind").GetString() == "Tag");
        Assert.Equal("Category", tags[0].GetProperty("kind").GetString()); // categories first
        var kinds = tags.Select(t => t.GetProperty("kind").GetString()).ToList();
        Assert.Equal(kinds.OrderBy(k => k, StringComparer.Ordinal), kinds);

        // The other household's tag is not visible to A even by name collision: A may reuse B's name.
        var reuse = await CreateTagAsync(a.Client, householdA, ownB);
        Assert.NotEqual(Guid.Empty, reuse);
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Adult_creates_own_tag_child_reads_only_and_global_tags_cannot_be_changed()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");
        var name = Name("Grill");

        using var created = await adult.Client.PostAsync(TagUrl(household), new { name = "  " + name + "  ", kind = "category" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.JsonAsync();
        Assert.Equal(name, body.GetProperty("name").GetString());
        Assert.Equal("Category", body.GetProperty("kind").GetString());
        Assert.False(body.GetProperty("isGlobal").GetBoolean());
        Assert.Equal(1, await CountAsync("Tags", "HouseholdId = @h AND Name = @n", ("@h", household), ("@n", name)));

        using var childRead = await child.Client.GetAsync(TagUrl(household));
        Assert.Equal(HttpStatusCode.OK, childRead.StatusCode);
        using var childCreate = await child.Client.PostAsync(TagUrl(household), new { name = Name("Nei"), kind = "Tag" });
        await childCreate.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Duplicates: own name (any case) and a global name are refused.
        using var again = await adult.Client.PostAsync(TagUrl(household), new { name = name.ToUpperInvariant(), kind = "Tag" });
        await again.AssertProblemAsync(HttpStatusCode.Conflict, "tag-exists");
        using var shadow = await adult.Client.PostAsync(TagUrl(household), new { name = "vegetar", kind = "Tag" });
        await shadow.AssertProblemAsync(HttpStatusCode.Conflict, "tag-exists");

        // Validation.
        foreach (var bad in new object[]
        {
            new { name = "", kind = "Tag" },
            new { name = new string('x', Tag.MaxNameLength + 1), kind = "Tag" },
            new { name = "Ok", kind = "Nope" },
            new { name = "Ok" },
        })
        {
            using var response = await adult.Client.PostAsync(TagUrl(household), bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }

        // There is no edit or delete route for tags.
        foreach (var response in new[]
        {
            await adult.Client.PutAsync(TagUrl(household, "/" + Vegetar), new { name = "Kjøttkaker", kind = "Tag" }),
            await adult.Client.DeleteAsync(TagUrl(household, "/" + Vegetar)),
        })
        {
            using (response)
            {
                Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
            }
        }

        Assert.Equal(1, await CountAsync("Tags", "Id = @t AND Name = N'Vegetar' AND HouseholdId IS NULL", ("@t", Vegetar)));
    }

    [Fact]
    public async Task Recipe_tags_are_saved_replaced_and_returned_in_detail_and_list()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var own = await CreateTagAsync(owner.Client, household, Name("Egen"));

        using var created = await owner.Client.PostAsync(Url(household), new
        {
            title = Name("Tacos"),
            servings = 4,
            tagIds = new[] { Vegetar, Middag, own, Vegetar }, // a repeated id is ignored
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.JsonAsync();
        var id = body.GetProperty("id").GetGuid();
        var tags = body.GetProperty("tags").EnumerateArray().ToList();
        Assert.Equal(3, tags.Count);
        Assert.Equal("Middag", tags[0].GetProperty("name").GetString()); // categories first
        Assert.Equal(3, await CountAsync("RecipeTags", "RecipeId = @r", ("@r", id)));

        // The list item carries the same tags.
        using var list = await owner.Client.GetAsync(Url(household));
        var item = Assert.Single((await list.JsonAsync()).GetProperty("items").EnumerateArray());
        Assert.Equal(3, item.GetProperty("tags").GetArrayLength());

        // PUT replaces: keep Vegetar, drop the rest, add Rask.
        using var updated = await owner.Client.PutAsync(Url(household, "/" + id), new { title = Name("Tacos"), servings = 4, tagIds = new[] { Vegetar, Rask } });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(["Rask", "Vegetar"], (await updated.JsonAsync()).GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order());
        Assert.Equal(2, await CountAsync("RecipeTags", "RecipeId = @r", ("@r", id)));

        // PUT without tagIds clears them; the recipe has no tags, not null.
        using var cleared = await owner.Client.PutAsync(Url(household, "/" + id), new { title = Name("Tacos"), servings = 4 });
        Assert.Equal(0, (await cleared.JsonAsync()).GetProperty("tags").GetArrayLength());
        Assert.Equal(0, await CountAsync("RecipeTags", "RecipeId = @r", ("@r", id)));

        // Deleting the recipe removes its links but not the tags.
        await owner.Client.PutAsync(Url(household, "/" + id), new { title = Name("Tacos"), servings = 4, tagIds = new[] { Vegetar, own } });
        using var deleted = await owner.Client.DeleteAsync(Url(household, "/" + id));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await CountAsync("RecipeTags", "RecipeId = @r", ("@r", id)));
        Assert.Equal(1, await CountAsync("Tags", "Id = @t", ("@t", own)));
    }

    [Fact]
    public async Task Another_households_or_unknown_tag_ids_are_refused_and_never_saved_and_child_cannot_tag()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        var householdB = await b.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(a, householdA, "Child");
        var bTag = await CreateTagAsync(b.Client, householdB, Name("Hemmelig"));

        using var foreign = await a.Client.PostAsync(Url(householdA), new { title = Name("R"), servings = 2, tagIds = new[] { Vegetar, bTag } });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.True((await foreign.JsonAsync()).GetProperty("errors").TryGetProperty("tagIds[1]", out _));
        using var unknown = await a.Client.PostAsync(Url(householdA), new { title = Name("R"), servings = 2, tagIds = new[] { Vegetar, Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(await foreign.ProblemShapeAsync(), await unknown.ProblemShapeAsync()); // "someone else's" looks like "does not exist"
        using var empty = await a.Client.PostAsync(Url(householdA), new { title = Name("R"), servings = 2, tagIds = new[] { Guid.Empty } });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var tooMany = Enumerable.Range(0, Foodplanner.Api.Features.Recipes.RecipeEndpoints.MaxTagsPerRecipe + 1).Select(_ => Vegetar).ToArray();
        using var many = await a.Client.PostAsync(Url(householdA), new { title = Name("R"), servings = 2, tagIds = tooMany });
        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);
        Assert.Equal(0, await CountAsync("Recipes", "HouseholdId = @h", ("@h", householdA)));

        // PUT: the existing recipe keeps its tags.
        var id = await CreateRecipeAsync(a.Client, householdA, Name("R"), [Vegetar]);
        using var foreignPut = await a.Client.PutAsync(Url(householdA, "/" + id), new { title = Name("R"), servings = 2, tagIds = new[] { bTag } });
        Assert.Equal(HttpStatusCode.BadRequest, foreignPut.StatusCode);
        Assert.Equal(1, await CountAsync("RecipeTags", "RecipeId = @r AND TagId = @t", ("@r", id), ("@t", Vegetar)));
        Assert.Equal(0, await CountAsync("RecipeTags", "TagId = @t", ("@t", bTag)));

        using var childCreate = await child.Client.PostAsync(Url(householdA), new { title = Name("R"), servings = 2, tagIds = new[] { Vegetar } });
        await childCreate.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var childPut = await child.Client.PutAsync(Url(householdA, "/" + id), new { title = Name("R"), servings = 2, tagIds = Array.Empty<Guid>() });
        await childPut.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(1, await CountAsync("RecipeTags", "RecipeId = @r", ("@r", id)));

        // B uses A's tag id: it is not found for B either (A's own tag).
        var aTag = await CreateTagAsync(a.Client, householdA, Name("AOnly"));
        using var bUses = await b.Client.PostAsync(Url(householdB), new { title = Name("R"), servings = 2, tagIds = new[] { aTag } });
        Assert.Equal(HttpStatusCode.BadRequest, bUses.StatusCode);
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Filters_combine_with_AND_each_pair_and_all_together()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var r1 = await CreateRecipeAsync(owner.Client, household, "Suppe A", [Vegetar, Rask], 5, 15, Salt);
        var r2 = await CreateRecipeAsync(owner.Client, household, "Suppe B", [Vegetar], 20, null, Flour);
        var r3 = await CreateRecipeAsync(owner.Client, household, "Grot C", [Vegetar, Rask], 10, 40, Salt, Flour);
        var r4 = await CreateRecipeAsync(owner.Client, household, "Suppe D", [Rask], null, null, Salt);
        var r5 = await CreateRecipeAsync(owner.Client, household, "Kake E", [], 30, 0);

        string Tags(params Guid[] ids) => string.Join("&", ids.Select(i => "tagIds=" + i));
        var cases = new (string Query, Guid[] Expected)[]
        {
            ("", [r1, r2, r3, r4, r5]),
            // single filters
            ("search=suppe", [r1, r2, r4]),
            (Tags(Vegetar), [r1, r2, r3]),
            (Tags(Vegetar, Rask), [r1, r3]),
            ("maxMinutes=30", [r1, r2, r5]), // r4 has no times, so it never matches a time limit
            ("maxMinutes=0", []),
            ($"ingredientId={Salt}", [r1, r3, r4]),
            ($"ingredientId={Salt}&ingredientId={Flour}", [r3]),
            // each pair
            ($"search=suppe&{Tags(Vegetar)}", [r1, r2]),
            ("search=suppe&maxMinutes=30", [r1, r2]),
            ($"search=suppe&ingredientId={Salt}", [r1, r4]),
            ($"{Tags(Vegetar)}&maxMinutes=30", [r1, r2]),
            ($"{Tags(Vegetar)}&ingredientId={Salt}", [r1, r3]),
            ($"maxMinutes=30&ingredientId={Salt}", [r1]),
            // all together
            ($"search=suppe&{Tags(Vegetar, Rask)}&maxMinutes=30&ingredientId={Salt}", [r1]),
            ($"search=suppe&{Tags(Vegetar)}&maxMinutes=30&ingredientId={Flour}", [r2]),
            // an unmatched member of the AND matches nothing
            ($"{Tags(Vegetar)}&ingredientId={Guid.NewGuid()}", []),
            ($"{Tags(Vegetar, Guid.NewGuid())}", []),
        };

        foreach (var (query, expected) in cases)
        {
            var (total, ids) = await ListAsync(owner.Client, household, query);
            Assert.True(expected.Order().SequenceEqual(ids.Order()), $"query '{query}': expected {expected.Length} recipes, got {ids.Count}");
            Assert.Equal(expected.Length, total);
        }

        // Boundary: exactly the limit matches (r1 and r2 are 20, r5 is 30).
        Assert.Equal(new[] { r1, r2 }.Order(), (await ListAsync(owner.Client, household, "maxMinutes=20")).Ids.Order());
    }

    [Fact]
    public async Task Filters_only_see_the_own_households_recipes_and_validate_their_input()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        var householdB = await b.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(a, householdA, "Child");
        await CreateRecipeAsync(a.Client, householdA, "Aa", [Vegetar], 5, 5, Salt);
        await CreateRecipeAsync(b.Client, householdB, "Bb", [Vegetar], 5, 5, Salt);

        Assert.Single((await ListAsync(a.Client, householdA, $"tagIds={Vegetar}&ingredientId={Salt}&maxMinutes=10")).Ids);
        Assert.Single((await ListAsync(child.Client, householdA, $"tagIds={Vegetar}")).Ids); // a Child may filter
        Assert.Equal(0, (await ListAsync(a.Client, householdA, $"search=Bb")).Total);

        var tooMany = string.Join("&", Enumerable.Range(0, 11).Select(_ => "tagIds=" + Guid.NewGuid()));
        foreach (var query in new[] { "maxMinutes=-1", "maxMinutes=99999", tooMany, "tagIds=" + Guid.Empty, "ingredientId=" + Guid.Empty, "tagIds=not-a-guid" })
        {
            using var response = await a.Client.GetAsync(Url(householdA, "?" + query));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Paging_is_stable_with_equal_titles_ordered_by_id_with_no_duplicates_or_gaps()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var created = new List<Guid>();
        foreach (var title in new[] { "Gryte", "Gryte", "Gryte", "Bolle", "Pai", "Pai", "Suppe", "Suppe", "Wok" })
        {
            created.Add(await CreateRecipeAsync(owner.Client, household, title, [Vegetar]));
        }

        // The database order (title, then id) is the reference; the API must reproduce it exactly.
        var expected = await OrderedIdsAsync(household);
        Assert.Equal(created.Order().ToList(), expected.Order().ToList());

        var seen = new List<Guid>();
        for (var offset = 0; offset < created.Count; offset += 4)
        {
            var (total, ids) = await ListAsync(owner.Client, household, $"tagIds={Vegetar}&limit=4&offset={offset}");
            Assert.Equal(created.Count, total);
            seen.AddRange(ids);
        }

        Assert.Equal(expected, seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());

        // Repeating a page gives the same page.
        Assert.Equal(
            (await ListAsync(owner.Client, household, "limit=3&offset=3")).Ids,
            (await ListAsync(owner.Client, household, "limit=3&offset=3")).Ids);

        // Stable under inserts: new recipes that sort after the pages already read change neither those pages nor the rest.
        var firstPage = (await ListAsync(owner.Client, household, "limit=4&offset=0")).Ids;
        var later = new[] { await CreateRecipeAsync(owner.Client, household, "Zzz 1"), await CreateRecipeAsync(owner.Client, household, "Zzz 2") };
        var secondPage = (await ListAsync(owner.Client, household, "limit=4&offset=4")).Ids;
        var thirdPage = (await ListAsync(owner.Client, household, "limit=4&offset=8")).Ids;
        Assert.Equal(expected.Take(4), firstPage);
        Assert.Equal(expected.Skip(4).Take(4), secondPage);
        Assert.Equal([expected[8], later[0], later[1]], thirdPage);
        Assert.Equal(expected.Count + later.Length, (await ListAsync(owner.Client, household, "limit=100")).Total);
    }

    private async Task<List<Guid>> OrderedIdsAsync(Guid household)
    {
        var ids = new List<Guid>();
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(db.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Recipes WHERE HouseholdId = @h ORDER BY Title, Id";
        command.Parameters.AddWithValue("@h", household);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
