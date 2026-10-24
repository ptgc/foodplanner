using System.Net;
using System.Text.Json;
using Foodplanner.Api.Features.Recipes;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Tests.Recipes;

public sealed class RecipeEndpointTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly Guid Salt = IngredientSeed.All.Single(r => r.Key == "salt").Id;
    private static readonly Guid Flour = IngredientSeed.All.Single(r => r.Key == "hvetemel").Id;
    private static readonly Guid Gram = UnitSeed.All.Single(r => r.Code == "g").Id;
    private static readonly Guid Dl = UnitSeed.All.Single(r => r.Code == "dl").Id;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AuthHost Host() => HouseholdHost.Create(db.ConnectionString);

    private static string Url(Guid household, string rest = "") => $"/api/households/{household}/recipes{rest}";

    private static string NewTitle(string prefix = "Pannekaker") => $"{prefix} {Guid.NewGuid():N}";

    private static object ValidBody(string? title = null) => new
    {
        title = title ?? NewTitle(),
        description = "  Tynne og gode  ",
        servings = 4,
        prepMinutes = 10,
        cookMinutes = 20,
        steps = new[] { new { text = "Bland" }, new { text = "Stek" }, new { text = "Server" } },
        ingredients = new object[]
        {
            new { ingredientId = Flour, quantity = 2.5m, unitId = Dl, note = " siktet " },
            new { ingredientId = Salt, quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null },
        },
    };

    private static async Task<Guid> CreateAsync(ApiClient client, Guid household, object? body = null)
    {
        using var response = await client.PostAsync(Url(household), body ?? ValidBody());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private async Task<int> CountAsync(string table, string where, params (string Name, object Value)[] parameters) =>
        (int)(await HouseholdScenario.ScalarAsync(db.ConnectionString, $"SELECT COUNT(*) FROM {table} WHERE {where}", parameters))!;

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

    private async Task AssertNoCrossHouseholdLinksAsync()
    {
        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }

    [Fact]
    public async Task Adult_creates_reads_updates_and_deletes_a_recipe_with_ordered_steps_and_structured_ingredients()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var title = NewTitle();

        using var created = await adult.Client.PostAsync(Url(household), ValidBody(title));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.JsonAsync();
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal(Url(household, "/" + id), created.Headers.Location?.OriginalString);
        Assert.Equal(title, body.GetProperty("title").GetString());
        Assert.Equal("Tynne og gode", body.GetProperty("description").GetString());
        Assert.Equal(4, body.GetProperty("servings").GetInt32());
        Assert.Equal(30, body.GetProperty("totalMinutes").GetInt32());
        Assert.Equal(["Bland", "Stek", "Server"], body.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("text").GetString()));
        var lines = body.GetProperty("ingredients").EnumerateArray().ToList();
        Assert.Equal("Hvetemel", lines[0].GetProperty("ingredientName").GetString());
        Assert.Equal(2.5m, lines[0].GetProperty("quantity").GetDecimal());
        Assert.Equal("dl", lines[0].GetProperty("unitCode").GetString());
        Assert.Equal("siktet", lines[0].GetProperty("note").GetString());
        Assert.Equal("Salt", lines[1].GetProperty("ingredientName").GetString());
        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("quantity").ValueKind); // to taste
        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("unitCode").ValueKind);

        // The owner reads the same recipe.
        using var read = await owner.Client.GetAsync(Url(household, "/" + id));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(body.ToString(), (await read.JsonAsync()).ToString());

        // PUT replaces fields, steps and ingredients (3 -> 1 steps, 2 -> 1 lines).
        var newTitle = NewTitle("Vafler");
        using var updated = await adult.Client.PutAsync(Url(household, "/" + id), new
        {
            title = newTitle,
            servings = 6,
            steps = new[] { new { text = "Vispe" } },
            ingredients = new[] { new { ingredientId = Salt, quantity = 1m, unitId = Gram, note = (string?)null } },
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = await updated.JsonAsync();
        Assert.Equal(newTitle, after.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("totalMinutes").ValueKind);
        Assert.Equal(["Vispe"], after.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("text").GetString()));
        Assert.Equal("g", Assert.Single(after.GetProperty("ingredients").EnumerateArray()).GetProperty("unitCode").GetString());
        Assert.Equal(1, await CountAsync("RecipeSteps", "RecipeId = @r", ("@r", id)));
        Assert.Equal(1, await CountAsync("RecipeIngredients", "RecipeId = @r", ("@r", id)));
        Assert.Equal(1, await CountAsync("Recipes", "Id = @r", ("@r", id)));

        using var deleted = await adult.Client.DeleteAsync(Url(household, "/" + id));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var gone = await owner.Client.GetAsync(Url(household, "/" + id));
        await gone.AssertProblemAsync(HttpStatusCode.NotFound, "recipe-not-found");
        Assert.Equal(0, await CountAsync("Recipes", "Id = @r", ("@r", id)));
        Assert.Equal(0, await CountAsync("RecipeSteps", "RecipeId = @r", ("@r", id)));
        Assert.Equal(0, await CountAsync("RecipeIngredients", "RecipeId = @r", ("@r", id)));
        using var deletedAgain = await adult.Client.DeleteAsync(Url(household, "/" + id));
        await deletedAgain.AssertProblemAsync(HttpStatusCode.NotFound, "recipe-not-found");
    }

    [Fact]
    public async Task Child_can_read_but_not_create_update_or_delete()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        var title = NewTitle();
        var id = await CreateAsync(owner.Client, household, ValidBody(title));

        using var list = await child.Client.GetAsync(Url(household, $"?search={Uri.EscapeDataString(title)}"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(id, Assert.Single((await list.JsonAsync()).GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        using var read = await child.Client.GetAsync(Url(household, "/" + id));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(3, (await read.JsonAsync()).GetProperty("steps").GetArrayLength());

        using var create = await child.Client.PostAsync(Url(household), ValidBody());
        await create.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var update = await child.Client.PutAsync(Url(household, "/" + id), ValidBody("Changed " + Guid.NewGuid().ToString("N")));
        await update.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var delete = await child.Client.DeleteAsync(Url(household, "/" + id));
        await delete.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        Assert.Equal(1, await CountAsync("Recipes", "HouseholdId = @h", ("@h", household)));
        Assert.Equal(1, await CountAsync("Recipes", "Id = @r AND Title = @t", ("@r", id), ("@t", title)));
        Assert.Equal(3, await CountAsync("RecipeSteps", "RecipeId = @r", ("@r", id)));
    }

    [Fact]
    public async Task Invalid_input_is_problem_details_naming_the_field_and_nothing_is_saved()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var tooManySteps = Enumerable.Range(0, Recipe.MaxSteps + 1).Select(i => new { text = "s" + i }).ToArray();
        var tooManyLines = Enumerable.Range(0, Recipe.MaxIngredients + 1).Select(_ => new { ingredientId = Salt, quantity = 1m, unitId = Gram, note = (string?)null }).ToArray();

        (string[] Keys, object Body)[] cases =
        [
            (["title"], new { title = "", servings = 4 }),
            (["title"], new { title = "   ", servings = 4 }),
            (["title"], new { title = new string('x', Recipe.MaxTitleLength + 1), servings = 4 }),
            (["title", "servings"], new { }),
            (["servings"], new { title = "ok", servings = 0 }),
            (["servings"], new { title = "ok", servings = Recipe.MaxServings + 1 }),
            (["servings"], new { title = "ok", servings = -3 }),
            (["description"], new { title = "ok", servings = 1, description = new string('d', Recipe.MaxDescriptionLength + 1) }),
            (["prepMinutes"], new { title = "ok", servings = 1, prepMinutes = -1 }),
            (["cookMinutes"], new { title = "ok", servings = 1, cookMinutes = Recipe.MaxMinutes + 1 }),
            (["steps[1].text"], new { title = "ok", servings = 1, steps = new[] { new { text = "fine" }, new { text = " " } } }),
            (["steps[0].text"], new { title = "ok", servings = 1, steps = new[] { new { text = new string('x', RecipeStep.MaxTextLength + 1) } } }),
            (["steps"], new { title = "ok", servings = 1, steps = tooManySteps }),
            (["ingredients"], new { title = "ok", servings = 1, ingredients = tooManyLines }),
            (["ingredients[0].quantity"], new { title = "ok", servings = 1, ingredients = new[] { new { ingredientId = Salt, quantity = 0m, unitId = Gram, note = "" } } }),
            (["ingredients[0].quantity"], new { title = "ok", servings = 1, ingredients = new[] { new { ingredientId = Salt, quantity = -2m, unitId = Gram, note = "" } } }),
            (["ingredients[0].quantity"], new { title = "ok", servings = 1, ingredients = new[] { new { ingredientId = Salt, quantity = 0.00001m, unitId = Gram, note = "" } } }),
            (["ingredients[0].unitId"], new { title = "ok", servings = 1, ingredients = new[] { new { ingredientId = Salt, quantity = 1m, unitId = (Guid?)null, note = "" } } }),
            (["ingredients[0].unitId"], new { title = "ok", servings = 1, ingredients = new[] { new { ingredientId = Salt, quantity = (decimal?)null, unitId = (Guid?)Gram, note = "" } } }),
            (["ingredients[0].ingredientId"], new { title = "ok", servings = 1, ingredients = new[] { new { ingredientId = (Guid?)null, quantity = 1m, unitId = (Guid?)Gram, note = "" } } }),
            (["ingredients[0].note"], new { title = "ok", servings = 1, ingredients = new[] { new { ingredientId = Salt, quantity = 1m, unitId = Gram, note = new string('n', RecipeIngredient.MaxNoteLength + 1) } } }),
        ];
        foreach (var (keys, body) in cases)
        {
            using var response = await owner.Client.PostAsync(Url(household), body);
            await AssertValidationAsync(response, keys);
        }

        // Free text instead of an ingredient id is not part of the contract: the field is ignored and the id is missing.
        using var freeText = await owner.Client.PostAsync(Url(household), new { title = "ok", servings = 1, ingredients = new[] { new { name = "2 dl melk" } } });
        await AssertValidationAsync(freeText, "ingredients[0].ingredientId");

        Assert.Equal(0, await CountAsync("Recipes", "HouseholdId = @h", ("@h", household)));

        // The boundaries are accepted.
        using var boundary = await owner.Client.PostAsync(Url(household), new
        {
            title = new string('t', Recipe.MaxTitleLength),
            servings = Recipe.MaxServings,
            prepMinutes = 0,
            cookMinutes = Recipe.MaxMinutes,
            steps = Enumerable.Range(0, Recipe.MaxSteps).Select(i => new { text = new string('s', RecipeStep.MaxTextLength) }).ToArray(),
            ingredients = new[] { new { ingredientId = Salt, quantity = 100000m, unitId = Gram, note = new string('n', RecipeIngredient.MaxNoteLength) } },
        });
        Assert.Equal(HttpStatusCode.Created, boundary.StatusCode);

        // The same checks apply to PUT, and a bad PUT changes nothing.
        var id = await CreateAsync(owner.Client, household);
        using var badPut = await owner.Client.PutAsync(Url(household, "/" + id), new { title = "", servings = 0 });
        await AssertValidationAsync(badPut, "title", "servings");
        Assert.Equal(3, await CountAsync("RecipeSteps", "RecipeId = @r", ("@r", id)));

        foreach (var query in new[] { "?limit=0", "?limit=101", "?offset=-1", $"?search={new string('x', 101)}" })
        {
            using var response = await owner.Client.GetAsync(Url(household, query));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Another_households_or_unknown_ids_are_refused_as_validation_errors_and_never_saved()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        var householdB = await b.Client.CreateHouseholdAsync();
        var name = NewTitle("Hemmelig");
        using var createdByB = await b.Client.PostAsync($"/api/households/{householdB}/ingredients", new { name, dimension = "Mass" });
        Assert.Equal(HttpStatusCode.Created, createdByB.StatusCode);
        var bIngredient = (await createdByB.JsonAsync()).GetProperty("id").GetGuid();

        object WithIngredient(Guid ingredient, Guid unit) => new
        {
            title = NewTitle(),
            servings = 2,
            ingredients = new[] { new { ingredientId = Salt, quantity = 1m, unitId = Gram, note = (string?)null }, new { ingredientId = ingredient, quantity = 1m, unitId = unit, note = (string?)null } },
        };

        using var foreign = await a.Client.PostAsync(Url(householdA), WithIngredient(bIngredient, Gram));
        await AssertValidationAsync(foreign, "ingredients[1].ingredientId");
        using var unknownIngredient = await a.Client.PostAsync(Url(householdA), WithIngredient(Guid.NewGuid(), Gram));
        await AssertValidationAsync(unknownIngredient, "ingredients[1].ingredientId");
        using var unknownUnit = await a.Client.PostAsync(Url(householdA), WithIngredient(Salt, Guid.NewGuid()));
        await AssertValidationAsync(unknownUnit, "ingredients[1].unitId");
        Assert.Equal(await foreign.ProblemShapeAsync(), await unknownIngredient.ProblemShapeAsync()); // "someone else's" looks like "does not exist"
        Assert.Equal(0, await CountAsync("Recipes", "HouseholdId = @h", ("@h", householdA)));
        Assert.Equal(0, await CountAsync("RecipeIngredients", "IngredientId = @i", ("@i", bIngredient)));

        // The same on PUT: the existing recipe is untouched.
        var id = await CreateAsync(a.Client, householdA);
        using var foreignPut = await a.Client.PutAsync(Url(householdA, "/" + id), WithIngredient(bIngredient, Gram));
        await AssertValidationAsync(foreignPut, "ingredients[1].ingredientId");
        Assert.Equal(2, await CountAsync("RecipeIngredients", "RecipeId = @r", ("@r", id)));
        Assert.Equal(0, await CountAsync("RecipeIngredients", "IngredientId = @i", ("@i", bIngredient)));

        // B can use its own ingredient, and A's recipe stays free of it.
        var idB = await CreateAsync(b.Client, householdB, WithIngredient(bIngredient, Gram));
        Assert.Equal(1, await CountAsync("RecipeIngredients", "RecipeId = @r AND IngredientId = @i", ("@r", idB), ("@i", bIngredient)));
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Another_households_recipes_are_forbidden_by_route_and_not_found_by_id()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        var householdB = await b.Client.CreateHouseholdAsync();
        var title = NewTitle("Privat");
        var idA = await CreateAsync(a.Client, householdA, ValidBody(title));

        // B uses A's route: not a member.
        foreach (var response in new[]
        {
            await b.Client.GetAsync(Url(householdA)),
            await b.Client.GetAsync(Url(householdA, "/" + idA)),
            await b.Client.PostAsync(Url(householdA), ValidBody()),
            await b.Client.PutAsync(Url(householdA, "/" + idA), ValidBody()),
            await b.Client.DeleteAsync(Url(householdA, "/" + idA)),
        })
        {
            using (response)
            {
                await response.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            }
        }

        // B uses its own route with A's recipe id: the filter hides it, so it does not exist.
        foreach (var response in new[]
        {
            await b.Client.GetAsync(Url(householdB, "/" + idA)),
            await b.Client.PutAsync(Url(householdB, "/" + idA), ValidBody("Overtatt " + Guid.NewGuid().ToString("N"))),
            await b.Client.DeleteAsync(Url(householdB, "/" + idA)),
        })
        {
            using (response)
            {
                await response.AssertProblemAsync(HttpStatusCode.NotFound, "recipe-not-found");
            }
        }

        using var listB = await b.Client.GetAsync(Url(householdB, $"?search={Uri.EscapeDataString(title)}"));
        Assert.Equal(0, (await listB.JsonAsync()).GetProperty("total").GetInt32());
        Assert.Equal(1, await CountAsync("Recipes", "Id = @r AND Title = @t AND HouseholdId = @h", ("@r", idA), ("@t", title), ("@h", householdA)));
        Assert.Equal(3, await CountAsync("RecipeSteps", "RecipeId = @r", ("@r", idA)));
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task List_is_paged_sorted_searchable_and_minimal()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        foreach (var title in new[] { "Cc suppe", "aa grøt", "Bb suppe", "Dd kake" })
        {
            await CreateAsync(owner.Client, household, new { title, servings = 2, prepMinutes = title.StartsWith("Cc", StringComparison.Ordinal) ? 5 : (int?)null, cookMinutes = title.StartsWith("Cc", StringComparison.Ordinal) ? 15 : (int?)null });
        }

        using var all = await owner.Client.GetAsync(Url(household));
        var first = await all.JsonAsync();
        Assert.Equal(4, first.GetProperty("total").GetInt32());
        Assert.Equal(RecipeEndpoints.DefaultLimit, first.GetProperty("limit").GetInt32());
        var items = first.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["aa grøt", "Bb suppe", "Cc suppe", "Dd kake"], items.Select(i => i.GetProperty("title").GetString()));
        Assert.Equal(["id", "servings", "tags", "title", "totalMinutes"], items[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(20, items[2].GetProperty("totalMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("totalMinutes").ValueKind);

        using var page = await owner.Client.GetAsync(Url(household, "?limit=2&offset=1"));
        var paged = await page.JsonAsync();
        Assert.Equal(4, paged.GetProperty("total").GetInt32());
        Assert.Equal(["Bb suppe", "Cc suppe"], paged.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()));

        using var search = await owner.Client.GetAsync(Url(household, "?search=SUPPE"));
        var found = await search.JsonAsync();
        Assert.Equal(2, found.GetProperty("total").GetInt32());
        using var none = await owner.Client.GetAsync(Url(household, "?search=" + Guid.NewGuid().ToString("N")));
        Assert.Equal(0, (await none.JsonAsync()).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_concurrent_delete_surfaces_as_409_not_a_server_error()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var id = await CreateAsync(owner.Client, household);

        await using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(db.ConnectionString).Options,
            new FixedHousehold(household, HouseholdRole.Adult),
            new TenantWriteScope(),
            TimeProvider.System);
        var recipe = await context.Recipes.SingleAsync(r => r.Id == id, Ct);
        recipe.Update("Changed", null, 2, null, null);
        // Someone else removes the row after it was loaded: the UPDATE (which also matches HouseholdId, T-008) affects no row.
        // A forged entity for another household's row fails the same way, with the same exception.
        Assert.Equal(1, (int)(await HouseholdScenario.ScalarAsync(db.ConnectionString, "DELETE FROM Recipes WHERE Id = @r; SELECT @@ROWCOUNT", ("@r", id)))!);

        var result = await RecipeEndpoints.TrySaveAsync(context, Ct);

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(409, problem.StatusCode);
        Assert.Equal("urn:foodplanner:problem:recipe-conflict", problem.ProblemDetails.Type);
    }
}
