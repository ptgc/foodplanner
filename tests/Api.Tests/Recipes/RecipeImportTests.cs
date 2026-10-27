using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Import;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Catalog;
using Foodplanner.Infrastructure.Persistence;

namespace Foodplanner.Api.Tests.Recipes;

public sealed class RecipeImportTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private const string PastedRecipe = """
        Mormors pannekaker
        Porsjoner: 4
        Forberedelse: 10 min
        Koketid: 20 min

        Ingredienser
        2,5 dl hvetemel
        5 dl melk
        3 egg
        1/2 ts salt
        2 dl dragefrukt
        smør til steking

        Fremgangsmåte
        1. Visp sammen mel og melk.
        2. Ha i eggene og saltet.
        3. Stek tynne pannekaker.
        """;

    private const string RecipePage = """
        <html><head><title>Pannekaker</title>
        <script type="application/ld+json">
        {"@context":"https://schema.org","@type":"Recipe","name":"Pannekaker fra nettet",
         "image":["https://img.example.test/p.jpg"],"recipeYield":"4 porsjoner","prepTime":"PT10M","cookTime":"PT20M",
         "recipeIngredient":["5 dl melk","3 egg","1 ss smør"],
         "recipeInstructions":[{"@type":"HowToStep","text":"Visp."},{"@type":"HowToStep","text":"Stek."}]}
        </script></head><body></body></html>
        """;

    private static readonly Guid Stk = UnitSeed.All.Single(u => u.Code == "stk").Id;
    private static readonly Guid Dl = UnitSeed.All.Single(u => u.Code == "dl").Id;
    private static readonly Guid Milk = IngredientSeed.All.Single(r => r.Key == "melk").Id;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AuthHost Host(FakeResolver? resolver = null, params (string Key, string Value)[] settings)
    {
        var host = AuthHost.Create(
            db.ConnectionString,
            settings.ToDictionary(s => s.Key, s => (string?)s.Value),
            configureServices: resolver is null ? null : ImportTestServices.Use(resolver));
        host.DefaultLanguage = "nb";
        return host;
    }

    private static string Url(Guid household, string rest = "") => $"/api/households/{household}/imports{rest}";

    private static async Task<JsonElement> CreateTextAsync(ApiClient client, Guid household, string text)
    {
        using var response = await client.PostAsync(Url(household), new { text });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.JsonAsync();
    }

    private async Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt64(await HouseholdScenario.ScalarAsync(db.ConnectionString, sql, parameters));

    private async Task AssertNoCrossHouseholdLinksAsync()
    {
        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }

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

    private static object FullDraft(JsonElement import, object[]? lineEdits = null) => new
    {
        title = import.GetProperty("title").GetString(),
        description = import.GetProperty("description").GetString(),
        servings = import.GetProperty("servings").GetInt32(),
        prepMinutes = import.GetProperty("prepMinutes").ValueKind == JsonValueKind.Null ? (int?)null : import.GetProperty("prepMinutes").GetInt32(),
        cookMinutes = import.GetProperty("cookMinutes").ValueKind == JsonValueKind.Null ? (int?)null : import.GetProperty("cookMinutes").GetInt32(),
        steps = import.GetProperty("steps").EnumerateArray().Select(s => s.GetString()).ToArray(),
        lines = lineEdits ?? [],
    };

    // ---- The flow ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Text_import_fix_the_unmatched_line_and_save_gives_a_recipe_with_structured_lines_only()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");

        // 1. Paste: a draft comes back with every line matched or flagged.
        var import = await CreateTextAsync(adult.Client, household, PastedRecipe);
        var importId = import.GetProperty("id").GetGuid();
        Assert.Equal("Text", import.GetProperty("source").GetString());
        Assert.Equal("Draft", import.GetProperty("status").GetString());
        Assert.Equal("Mormors pannekaker", import.GetProperty("title").GetString());
        Assert.Equal(4, import.GetProperty("servings").GetInt32());
        Assert.Equal(10, import.GetProperty("prepMinutes").GetInt32());
        Assert.Equal(20, import.GetProperty("cookMinutes").GetInt32());
        Assert.Equal(3, import.GetProperty("steps").GetArrayLength());
        Assert.False(import.GetProperty("canSave").GetBoolean());
        var lines = import.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(6, lines.Count);
        Assert.Equal(["Matched", "Matched", "Matched", "Matched", "UnmatchedIngredient", "Matched"], lines.Select(l => l.GetProperty("status").GetString()));
        Assert.Equal("Hvetemel", lines[0].GetProperty("ingredientName").GetString());
        Assert.Equal(2.5m, lines[0].GetProperty("quantity").GetDecimal());
        Assert.Equal("dl", lines[0].GetProperty("unitCode").GetString());
        Assert.Equal("2,5 dl hvetemel", lines[0].GetProperty("original").GetString());
        Assert.Equal("stk", lines[2].GetProperty("unitCode").GetString());
        Assert.Equal(JsonValueKind.Null, lines[5].GetProperty("quantity").ValueKind); // "smør til steking": to taste
        Assert.Equal("til steking", lines[5].GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, lines[4].GetProperty("ingredientId").ValueKind);
        Assert.Equal("dragefrukt", lines[4].GetProperty("ingredientText").GetString());

        // 2. Saving now is refused and points at the unmatched line.
        using (var early = await adult.Client.PostAsync(Url(household, $"/{importId}/save")))
        {
            await AssertValidationAsync(early, "lines[4].ingredientId");
        }

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Recipes WHERE HouseholdId = @h", ("@h", household)));

        // 3. Create the household's own ingredient, then map the line to it.
        using var created = await adult.Client.PostAsync($"/api/households/{household}/ingredients", new { name = "Dragefrukt", isStaple = false, dimension = "Count" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dragonFruit = (await created.JsonAsync()).GetProperty("id").GetGuid();

        using var fixedUp = await adult.Client.PutAsync(
            Url(household, "/" + importId),
            FullDraft(import, [new { position = 4, ingredientId = dragonFruit, quantity = 2m, unitId = Stk, note = (string?)null }]));
        Assert.Equal(HttpStatusCode.OK, fixedUp.StatusCode);
        var updated = await fixedUp.JsonAsync();
        Assert.True(updated.GetProperty("canSave").GetBoolean());
        var fixedLine = updated.GetProperty("lines")[4];
        Assert.Equal("Matched", fixedLine.GetProperty("status").GetString());
        Assert.Equal("Dragefrukt", fixedLine.GetProperty("ingredientName").GetString());
        Assert.Equal("2 dl dragefrukt", fixedLine.GetProperty("original").GetString()); // the artifact keeps what was imported

        // While it is a draft the original text is kept in the artifact.
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM RecipeImports WHERE Id = @i AND Status = 'Draft' AND RawContent LIKE N'%2,5 dl hvetemel%'", ("@i", importId)));

        // 4. Save: a recipe through the normal path.
        using var saved = await adult.Client.PostAsync(Url(household, $"/{importId}/save"));
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var recipeId = (await saved.JsonAsync()).GetProperty("recipeId").GetGuid();
        Assert.Equal($"/api/households/{household}/recipes/{recipeId}", saved.Headers.Location?.OriginalString);

        using var recipeResponse = await owner.Client.GetAsync($"/api/households/{household}/recipes/{recipeId}");
        var recipe = await recipeResponse.JsonAsync();
        Assert.Equal("Mormors pannekaker", recipe.GetProperty("title").GetString());
        Assert.Equal(4, recipe.GetProperty("servings").GetInt32());
        Assert.Equal(30, recipe.GetProperty("totalMinutes").GetInt32());
        Assert.Equal(["Visp sammen mel og melk.", "Ha i eggene og saltet.", "Stek tynne pannekaker."], recipe.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("text").GetString()));
        var recipeLines = recipe.GetProperty("ingredients").EnumerateArray().ToList();
        Assert.Equal(["Hvetemel", "Melk", "Egg", "Salt", "Dragefrukt", "Smør"], recipeLines.Select(l => l.GetProperty("ingredientName").GetString()));
        Assert.Equal(2.5m, recipeLines[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(0.5m, recipeLines[3].GetProperty("quantity").GetDecimal());
        Assert.Equal("ts", recipeLines[3].GetProperty("unitCode").GetString());
        Assert.Equal(JsonValueKind.Null, recipeLines[5].GetProperty("quantity").ValueKind);

        // 5. Rule 5: the original text is in the artifact only; the recipe holds ids, numbers and a short note.
        Assert.DoesNotContain("2,5 dl hvetemel", recipe.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM RecipeImports WHERE Id = @i AND Status = 'Saved' AND SavedRecipeId = @r AND RawContent IS NULL AND DraftJson IS NULL", ("@i", importId), ("@r", recipeId)));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM RecipeImports WHERE HouseholdId = @h AND (RawContent LIKE N'%hvetemel%' OR DraftJson LIKE N'%hvetemel%')", ("@h", household)));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM RecipeIngredients WHERE RecipeId = @r AND Note LIKE N'%dl %'", ("@r", recipeId)));

        // 6. The import is done: it cannot be edited, saved or discarded again.
        using (var again = await adult.Client.PostAsync(Url(household, $"/{importId}/save")))
        {
            await again.AssertProblemAsync(HttpStatusCode.Conflict, "import-not-draft");
        }

        using (var edit = await adult.Client.PutAsync(Url(household, "/" + importId), FullDraft(import)))
        {
            await edit.AssertProblemAsync(HttpStatusCode.Conflict, "import-not-draft");
        }

        using (var discard = await adult.Client.DeleteAsync(Url(household, "/" + importId)))
        {
            await discard.AssertProblemAsync(HttpStatusCode.Conflict, "import-not-draft");
        }

        using var readBack = await adult.Client.GetAsync(Url(household, "/" + importId));
        var final = await readBack.JsonAsync();
        Assert.Equal("Saved", final.GetProperty("status").GetString());
        Assert.Equal(recipeId, final.GetProperty("savedRecipeId").GetGuid());
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Edits_change_the_title_steps_and_a_line_and_can_remove_a_line()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var import = await CreateTextAsync(owner.Client, household, "Suppe\nIngredienser\n1 l vann\n1 løk\nFremgangsmåte\nKok.");
        var id = import.GetProperty("id").GetGuid();
        Assert.Equal(2, import.GetProperty("lines").GetArrayLength());

        using var response = await owner.Client.PutAsync(Url(household, "/" + id), new
        {
            title = "  Løksuppe  ",
            description = "God",
            servings = 2,
            prepMinutes = 5,
            cookMinutes = (int?)null,
            steps = new[] { " Kok vannet. ", "Ha i løken." },
            lines = new object[]
            {
                new { position = 0, ingredientId = Milk, quantity = 1m, unitId = Dl, note = " kald " },
                new { position = 1, remove = true },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal("Løksuppe", body.GetProperty("title").GetString());
        Assert.Equal("God", body.GetProperty("description").GetString());
        Assert.Equal(2, body.GetProperty("servings").GetInt32());
        Assert.Equal(["Kok vannet.", "Ha i løken."], body.GetProperty("steps").EnumerateArray().Select(s => s.GetString()));
        var line = Assert.Single(body.GetProperty("lines").EnumerateArray());
        Assert.Equal("Melk", line.GetProperty("ingredientName").GetString());
        Assert.Equal("kald", line.GetProperty("note").GetString());
        Assert.Equal("1 l vann", line.GetProperty("original").GetString());
        Assert.True(body.GetProperty("canSave").GetBoolean());

        using var reread = await owner.Client.GetAsync(Url(household, "/" + id));
        Assert.Equal(body.ToString(), (await reread.JsonAsync()).ToString());
    }

    [Fact]
    public async Task A_line_can_be_set_to_taste_and_unmatched_units_block_saving()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var import = await CreateTextAsync(owner.Client, household, "Tomatsuppe\nIngredienser\n1 boks hakkede tomater\nFremgangsmåte\nKok.");
        var id = import.GetProperty("id").GetGuid();
        var line = import.GetProperty("lines")[0];
        Assert.Equal("UnmatchedUnit", line.GetProperty("status").GetString());
        Assert.Equal("boks", line.GetProperty("unitText").GetString());
        Assert.NotEqual(JsonValueKind.Null, line.GetProperty("ingredientId").ValueKind);

        using (var blocked = await owner.Client.PostAsync(Url(household, $"/{id}/save")))
        {
            await AssertValidationAsync(blocked, "lines[0].unitId");
        }

        // Give it a real unit.
        var grams = UnitSeed.All.Single(u => u.Code == "g").Id;
        using var fixedUp = await owner.Client.PutAsync(Url(household, "/" + id), FullDraft(import, [new
        {
            position = 0,
            ingredientId = line.GetProperty("ingredientId").GetGuid(),
            quantity = 400m,
            unitId = grams,
            note = "boks",
        }]));
        Assert.True((await fixedUp.JsonAsync()).GetProperty("canSave").GetBoolean());

        using var saved = await owner.Client.PostAsync(Url(household, $"/{id}/save"));
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
    }

    [Fact]
    public async Task Resolving_an_ingredient_keeps_the_parsed_quantity_unit_and_note_and_to_taste_is_explicit()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var import = await CreateTextAsync(
            owner.Client, household,
            "Gryte\nIngredienser\n400 g dragefrukt, hakket\n400 g kjøttdeig av svin\n0,5 ts pepper\nFremgangsmåte\nKok.");
        var id = import.GetProperty("id").GetGuid();
        var lines = import.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal("UnmatchedIngredient", lines[0].GetProperty("status").GetString());
        Assert.Equal(400m, lines[0].GetProperty("quantity").GetDecimal());

        using var created = await owner.Client.PostAsync($"/api/households/{household}/ingredients", new { name = "Dragefrukt", isStaple = false, dimension = "Mass" });
        var dragonFruit = (await created.JsonAsync()).GetProperty("id").GetGuid();

        // Only position + ingredientId, as a search pick or a suggestion pick sends it.
        var minced = lines[1].GetProperty("suggestions").EnumerateArray().Select(s => (Guid?)s.GetProperty("ingredientId").GetGuid()).FirstOrDefault()
            ?? IngredientSeed.All.Single(r => r.Key == "kjottdeig").Id;
        var pepper = IngredientSeed.All.Single(r => r.Key == "sort_pepper").Id;
        using var put = await owner.Client.PutAsync(Url(household, "/" + id), new
        {
            lines = new object[]
            {
                new { position = 0, ingredientId = dragonFruit },
                new { position = 1, ingredientId = minced },
                new { position = 2, ingredientId = pepper },
            },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = (await put.JsonAsync()).GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(400m, updated[0].GetProperty("quantity").GetDecimal());
        Assert.Equal("g", updated[0].GetProperty("unitCode").GetString());
        Assert.Equal("hakket", updated[0].GetProperty("note").GetString());
        Assert.Equal(400m, updated[1].GetProperty("quantity").GetDecimal());
        Assert.Equal(0.5m, updated[2].GetProperty("quantity").GetDecimal());

        using var saved = await owner.Client.PostAsync(Url(household, $"/{id}/save"));
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var recipeId = (await saved.JsonAsync()).GetProperty("recipeId").GetGuid();
        using var recipeResponse = await owner.Client.GetAsync($"/api/households/{household}/recipes/{recipeId}");
        var recipeLines = (await recipeResponse.JsonAsync()).GetProperty("ingredients").EnumerateArray().ToList();
        Assert.Equal(400m, recipeLines[0].GetProperty("quantity").GetDecimal());
        Assert.Equal("g", recipeLines[0].GetProperty("unitCode").GetString());
        Assert.Equal(400m, recipeLines[1].GetProperty("quantity").GetDecimal());
        Assert.Equal("g", recipeLines[1].GetProperty("unitCode").GetString());
        Assert.Equal(0.5m, recipeLines[2].GetProperty("quantity").GetDecimal());
        Assert.Equal("ts", recipeLines[2].GetProperty("unitCode").GetString());

        // Explicit "to taste" still clears; it cannot be combined with an amount.
        var second = await CreateTextAsync(owner.Client, household, "Gryte\nIngredienser\n2 dl melk\nFremgangsmåte\nKok.");
        var secondId = second.GetProperty("id").GetGuid();
        using (var refused = await owner.Client.PutAsync(
            Url(household, "/" + secondId),
            new { lines = new object[] { new { position = 0, ingredientId = Milk, toTaste = true, quantity = 1m, unitId = Dl } } }))
        {
            await AssertValidationAsync(refused, "lines[0].quantity");
        }

        using var taste = await owner.Client.PutAsync(Url(household, "/" + secondId), new { lines = new object[] { new { position = 0, ingredientId = Milk, toTaste = true } } });
        var tasteLine = (await taste.JsonAsync()).GetProperty("lines")[0];
        Assert.Equal(JsonValueKind.Null, tasteLine.GetProperty("quantity").ValueKind);
        Assert.Equal(JsonValueKind.Null, tasteLine.GetProperty("unitId").ValueKind);
    }

    // ---- URL imports ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Url_import_reads_the_json_ld_recipe_and_keeps_only_the_image_url_as_a_candidate()
    {
        var resolver = ImportTestServices.Resolver(("recipes.test", "127.0.0.1"));
        await using var server = new TestHttpServer((_, stream, ct) => TestHttpServer.Ok(stream, "text/html; charset=utf-8", RecipePage, ct));
        await using var host = Host(resolver);
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var response = await owner.Client.PostAsync(Url(household), new { url = server.Url("/pannekaker") });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal("Url", body.GetProperty("source").GetString());
        Assert.Equal(server.Url("/pannekaker"), body.GetProperty("sourceUrl").GetString());
        Assert.Equal("Pannekaker fra nettet", body.GetProperty("title").GetString());
        Assert.Equal("https://img.example.test/p.jpg", body.GetProperty("imageUrl").GetString());
        Assert.Equal(4, body.GetProperty("servings").GetInt32());
        Assert.Equal(["Matched", "Matched", "Matched"], body.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("status").GetString()));
        Assert.True(body.GetProperty("canSave").GetBoolean());
        Assert.Single(server.Seen); // only the page: the image was not downloaded

        var id = body.GetProperty("id").GetGuid();
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM RecipeImports WHERE Id = @i AND Source = 'Url' AND RawContent LIKE N'%recipeIngredient%'", ("@i", id)));

        using var saved = await owner.Client.PostAsync(Url(household, $"/{id}/save"));
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        // Q-017: the original (JSON-LD) and the draft are erased on save; the URL and the recipe id stay.
        Assert.Equal(
            1,
            await CountAsync(
                "SELECT COUNT(*) FROM RecipeImports WHERE Id = @i AND Status = 'Saved' AND RawContent IS NULL AND DraftJson IS NULL AND SourceUrl = @u AND SavedRecipeId IS NOT NULL",
                ("@i", id), ("@u", server.Url("/pannekaker"))));
    }

    [Fact]
    public async Task Url_import_failures_are_problem_details_and_store_nothing()
    {
        var resolver = ImportTestServices.Resolver(("recipes.test", "127.0.0.1"), ("internal.test", "10.0.0.5"));
        await using var plain = new TestHttpServer((_, stream, ct) => TestHttpServer.Ok(stream, "text/html", "<html><body>no recipe</body></html>", ct));
        await using var text = new TestHttpServer((_, stream, ct) => TestHttpServer.Ok(stream, "text/plain", "hello", ct));
        await using var big = new TestHttpServer((_, stream, ct) => TestHttpServer.Ok(stream, "text/html", new string('a', 5000), ct));
        await using var missing = new TestHttpServer((_, stream, ct) =>
            TestHttpServer.WriteAsync(stream, "404 Not Found", [("Content-Type", "text/html"), ("Content-Length", "0")], [], ct));
        await using var slow = new TestHttpServer((_, _, ct) => Task.Delay(TimeSpan.FromSeconds(30), ct));
        await using var host = Host(resolver, ("RecipeImport:MaxBytes", "2000"), ("RecipeImport:TimeoutSeconds", "1"));
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        async Task AssertFailsAsync(string url, HttpStatusCode status, string code)
        {
            using var response = await owner.Client.PostAsync(Url(household), new { url });
            await response.AssertProblemAsync(status, code);
        }

        await AssertFailsAsync(plain.Url(), HttpStatusCode.UnprocessableEntity, "import-no-recipe-found");
        await AssertFailsAsync(text.Url(), HttpStatusCode.UnprocessableEntity, "import-unsupported-content");
        await AssertFailsAsync(big.Url(), HttpStatusCode.UnprocessableEntity, "import-too-large");
        await AssertFailsAsync(missing.Url(), HttpStatusCode.BadGateway, "import-fetch-failed");
        await AssertFailsAsync(slow.Url(), HttpStatusCode.GatewayTimeout, "import-fetch-timeout");
        await AssertFailsAsync("http://nowhere.test/", HttpStatusCode.BadGateway, "import-fetch-failed");

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM RecipeImports WHERE HouseholdId = @h", ("@h", household)));
    }

    [Theory]
    [InlineData("http://internal.test/recipe")] // resolves to 10.0.0.5
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("ftp://recipes.test/recipe")]
    [InlineData("http://user:pw@recipes.test/")]
    public async Task Urls_that_are_not_public_web_pages_are_refused_with_one_answer(string url)
    {
        // The test policy allows loopback and any port, so here only a name that points at a private address, a private literal,
        // another scheme and credentials are refused; the next test uses the production wiring, which refuses loopback and ports as well.
        var resolver = ImportTestServices.Resolver(("recipes.test", "127.0.0.1"), ("internal.test", "10.0.0.5"));
        await using var host = Host(resolver);
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var response = await owner.Client.PostAsync(Url(household), new { url });

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "import-url-not-allowed");
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM RecipeImports WHERE HouseholdId = @h", ("@h", household)));
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://localhost/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://example.com:8080/")]
    [InlineData("https://example.com:22/")]
    [InlineData("file:///etc/passwd")]
    public async Task With_the_production_wiring_loopback_private_and_unusual_ports_are_refused(string url)
    {
        await using var host = Host(); // no test resolver or policy: the real registrations
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var response = await owner.Client.PostAsync(Url(household), new { url });

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "import-url-not-allowed");
    }

    [Fact]
    public async Task Url_imports_are_rate_limited_per_user_and_text_imports_are_not_counted()
    {
        var resolver = ImportTestServices.Resolver(("recipes.test", "127.0.0.1"));
        await using var server = new TestHttpServer((_, stream, ct) => TestHttpServer.Ok(stream, "text/html", RecipePage, ct));
        await using var host = Host(resolver, ("RateLimiting:UrlImport:PermitLimit", "2"), ("RateLimiting:UrlImport:WindowSeconds", "600"));
        var first = await host.SignInAsync();
        var household = await first.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(first, household, "Adult");

        for (var i = 0; i < 2; i++)
        {
            using var ok = await first.Client.PostAsync(Url(household), new { url = server.Url() });
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        using var limited = await first.Client.PostAsync(Url(household), new { url = server.Url() });
        await limited.AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate-limited");
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Equal(2, server.Seen.Count); // the third request never fetched anything

        // Text imports fetch nothing and are not limited; another user has their own allowance.
        await CreateTextAsync(first.Client, household, "Suppe\n1 l vann");
        using var other = await adult.Client.PostAsync(Url(household), new { url = server.Url() });
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.Equal(3, server.Seen.Count);
    }

    // ---- Validation -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_request_needs_exactly_one_of_url_and_text_and_a_recipe_in_the_text()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        foreach (var (body, key) in new (object Body, string Key)[]
        {
            (new { }, "url"),
            (new { url = "https://example.com/", text = "Suppe\n1 l vann" }, "url"),
            (new { text = "   " }, "url"),
            (new { url = new string('a', 2001) }, "url"),
            (new { text = new string('a', 100_001) }, "text"),
            (new { text = "\n\n   \n" }, "url"),
        })
        {
            using var response = await owner.Client.PostAsync(Url(household), body);
            await AssertValidationAsync(response, key);
        }

        using var empty = await owner.Client.PostAsync(Url(household));
        await AssertValidationAsync(empty, "url");
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM RecipeImports WHERE HouseholdId = @h", ("@h", household)));
    }

    [Fact]
    public async Task Edits_are_validated_and_ids_are_resolved_through_the_tenant_filter()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        var householdB = await b.Client.CreateHouseholdAsync();
        using var foreign = await b.Client.PostAsync($"/api/households/{householdB}/ingredients", new { name = "Hemmelig " + Guid.NewGuid().ToString("N"), dimension = "Mass" });
        var foreignIngredient = (await foreign.JsonAsync()).GetProperty("id").GetGuid();
        var import = await CreateTextAsync(a.Client, householdA, "Suppe\nIngredienser\n1 l vann\n1 løk\nFremgangsmåte\nKok.");
        var id = import.GetProperty("id").GetGuid();
        var before = import.ToString();

        async Task AssertEditRefusedAsync(object edit, params string[] keys)
        {
            using var response = await a.Client.PutAsync(Url(householdA, "/" + id), FullDraft(import, [edit]));
            await AssertValidationAsync(response, keys);
        }

        // Another household's ingredient is "not found", exactly like an unknown id.
        await AssertEditRefusedAsync(new { position = 0, ingredientId = foreignIngredient, quantity = 1m, unitId = Dl }, "lines[0].ingredientId");
        await AssertEditRefusedAsync(new { position = 0, ingredientId = Guid.NewGuid(), quantity = 1m, unitId = Dl }, "lines[0].ingredientId");
        await AssertEditRefusedAsync(new { position = 0, ingredientId = Milk, quantity = 1m, unitId = Guid.NewGuid() }, "lines[0].unitId");
        await AssertEditRefusedAsync(new { position = 0, ingredientId = Milk, quantity = 1m }, "lines[0].unitId");
        await AssertEditRefusedAsync(new { position = 0, ingredientId = Milk, unitId = Dl }, "lines[0].unitId");
        await AssertEditRefusedAsync(new { position = 0, ingredientId = Milk, quantity = 0m, unitId = Dl }, "lines[0].quantity");
        await AssertEditRefusedAsync(new { position = 0, quantity = 1m, unitId = Dl }, "lines[0].ingredientId");
        await AssertEditRefusedAsync(new { position = 5, ingredientId = Milk }, "lines[0].position");
        await AssertEditRefusedAsync(new { position = -1, ingredientId = Milk }, "lines[0].position");
        await AssertEditRefusedAsync(new { ingredientId = Milk }, "lines[0].position");
        await AssertEditRefusedAsync(new { position = 0, ingredientId = Milk, note = new string('x', 201) }, "lines[0].note");

        using (var duplicate = await a.Client.PutAsync(Url(householdA, "/" + id), FullDraft(import, [new { position = 0, remove = true }, new { position = 0, remove = true }])))
        {
            await AssertValidationAsync(duplicate, "lines[1].position");
        }

        using (var fields = await a.Client.PutAsync(Url(householdA, "/" + id), new
        {
            title = new string('t', 201),
            description = new string('d', 2001),
            servings = 0,
            prepMinutes = -1,
            cookMinutes = 2000,
            steps = new[] { "", new string('s', 2001) },
            lines = Array.Empty<object>(),
        }))
        {
            await AssertValidationAsync(fields, "title", "description", "servings", "prepMinutes", "cookMinutes", "steps[0]", "steps[1]");
        }

        // An empty edit (no fields, no lines) changes nothing.
        using (var empty = await a.Client.PutAsync(Url(householdA, "/" + id)))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        }

        // Nothing was changed by any refused edit.
        using var reread = await a.Client.GetAsync(Url(householdA, "/" + id));
        Assert.Equal(before, (await reread.JsonAsync()).ToString());
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Saving_without_a_title_is_a_validation_error_and_writes_nothing()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var import = await CreateTextAsync(owner.Client, household, "Suppe\nIngredienser\n1 l vann\nFremgangsmåte\nKok.");
        var id = import.GetProperty("id").GetGuid();
        using var cleared = await owner.Client.PutAsync(Url(household, "/" + id), new { title = "", servings = 4, steps = new[] { "Kok." }, lines = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        using var save = await owner.Client.PostAsync(Url(household, $"/{id}/save"));

        await AssertValidationAsync(save, "title");
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Recipes WHERE HouseholdId = @h", ("@h", household)));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM RecipeImports WHERE Id = @i AND Status = 'Draft'", ("@i", id)));
    }

    // ---- Authorization and tenancy ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_child_and_a_non_member_cannot_use_any_import_endpoint()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var outsider = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        var import = await CreateTextAsync(owner.Client, household, "Suppe\nIngredienser\n1 l vann\nFremgangsmåte\nKok.");
        var id = import.GetProperty("id").GetGuid();

        foreach (var user in new[] { child, outsider })
        {
            foreach (var response in new[]
            {
                await user.Client.GetAsync(Url(household)),
                await user.Client.PostAsync(Url(household), new { text = "Suppe\n1 l vann" }),
                await user.Client.GetAsync(Url(household, "/" + id)),
                await user.Client.PutAsync(Url(household, "/" + id), FullDraft(import)),
                await user.Client.PostAsync(Url(household, $"/{id}/save")),
                await user.Client.DeleteAsync(Url(household, "/" + id)),
            })
            {
                using (response)
                {
                    await response.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
                }
            }
        }

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM RecipeImports WHERE HouseholdId = @h AND Status = 'Draft'", ("@h", household)));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Recipes WHERE HouseholdId = @h", ("@h", household)));
    }

    [Fact]
    public async Task Another_households_import_does_not_exist_for_you()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        var householdB = await b.Client.CreateHouseholdAsync();
        var importA = await CreateTextAsync(a.Client, householdA, "Hemmelig suppe\nIngredienser\n1 l vann\nFremgangsmåte\nKok.");
        var id = importA.GetProperty("id").GetGuid();

        foreach (var response in new[]
        {
            await b.Client.GetAsync(Url(householdB, "/" + id)),
            await b.Client.PutAsync(Url(householdB, "/" + id), FullDraft(importA)),
            await b.Client.PostAsync(Url(householdB, $"/{id}/save")),
            await b.Client.DeleteAsync(Url(householdB, "/" + id)),
        })
        {
            using (response)
            {
                await response.AssertProblemAsync(HttpStatusCode.NotFound, "import-not-found");
            }
        }

        using var listB = await b.Client.GetAsync(Url(householdB));
        Assert.Equal(0, (await listB.JsonAsync()).GetArrayLength());
        using var listA = await a.Client.GetAsync(Url(householdA));
        var summary = Assert.Single((await listA.JsonAsync()).EnumerateArray());
        Assert.Equal(id, summary.GetProperty("id").GetGuid());
        Assert.Equal("Hemmelig suppe", summary.GetProperty("title").GetString());
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM RecipeImports WHERE Id = @i AND Status = 'Draft' AND HouseholdId = @h", ("@i", id), ("@h", householdA)));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Recipes WHERE HouseholdId = @h", ("@h", householdB)));
    }

    [Fact]
    public async Task The_matcher_never_offers_another_households_ingredient()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        var householdB = await b.Client.CreateHouseholdAsync();
        var secret = "Xyzzyplugh" + Guid.NewGuid().ToString("N")[..6];
        using var created = await a.Client.PostAsync($"/api/households/{householdA}/ingredients", new { name = secret, dimension = "Mass" });
        var secretId = (await created.JsonAsync()).GetProperty("id").GetGuid();

        var forA = await CreateTextAsync(a.Client, householdA, $"Suppe\nIngredienser\n1 dl {secret}\nFremgangsmåte\nKok.");
        var forB = await CreateTextAsync(b.Client, householdB, $"Suppe\nIngredienser\n1 dl {secret}\nFremgangsmåte\nKok.");

        var lineA = forA.GetProperty("lines")[0];
        Assert.Equal(secretId, lineA.GetProperty("ingredientId").GetGuid()); // its own ingredient matches
        Assert.Equal(1.0, lineA.GetProperty("confidence").GetDouble());
        var lineB = forB.GetProperty("lines")[0];
        Assert.Equal("UnmatchedIngredient", lineB.GetProperty("status").GetString());
        Assert.DoesNotContain(secretId.ToString(), forB.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, lineB.GetProperty("suggestions").GetArrayLength());
        await AssertNoCrossHouseholdLinksAsync();
    }

    // ---- Discard, limits, races -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Discarding_erases_the_original_text_and_hides_the_import()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var import = await CreateTextAsync(owner.Client, household, PastedRecipe);
        var id = import.GetProperty("id").GetGuid();

        using var discard = await owner.Client.DeleteAsync(Url(household, "/" + id));

        Assert.Equal(HttpStatusCode.NoContent, discard.StatusCode);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM RecipeImports WHERE Id = @i AND Status = 'Discarded' AND RawContent IS NULL AND DraftJson IS NULL", ("@i", id)));
        foreach (var response in new[]
        {
            await owner.Client.GetAsync(Url(household, "/" + id)),
            await owner.Client.PutAsync(Url(household, "/" + id), FullDraft(import)),
            await owner.Client.PostAsync(Url(household, $"/{id}/save")),
            await owner.Client.DeleteAsync(Url(household, "/" + id)),
        })
        {
            using (response)
            {
                await response.AssertProblemAsync(HttpStatusCode.NotFound, "import-not-found");
            }
        }

        using var list = await owner.Client.GetAsync(Url(household));
        Assert.Equal(0, (await list.JsonAsync()).GetArrayLength());
    }

    [Fact]
    public async Task A_household_can_hold_only_so_many_drafts()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        Guid first = default;
        for (var i = 0; i < Foodplanner.Api.Features.Recipes.RecipeImportEndpoints.MaxDrafts; i++)
        {
            var created = await CreateTextAsync(owner.Client, household, $"Suppe {i}\n1 l vann");
            if (i == 0)
            {
                first = created.GetProperty("id").GetGuid();
            }
        }

        using (var over = await owner.Client.PostAsync(Url(household), new { text = "En til\n1 l vann" }))
        {
            await over.AssertProblemAsync(HttpStatusCode.Conflict, "import-limit");
        }

        using (var discard = await owner.Client.DeleteAsync(Url(household, "/" + first)))
        {
            Assert.Equal(HttpStatusCode.NoContent, discard.StatusCode);
        }

        await CreateTextAsync(owner.Client, household, "Nå går det\n1 l vann");
    }

    [Fact]
    public async Task Two_saves_at_once_make_one_recipe()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var title = "Dobbel " + Guid.NewGuid().ToString("N");
        var import = await CreateTextAsync(owner.Client, household, $"{title}\nIngredienser\n1 l vann\nFremgangsmåte\nKok.");
        var id = import.GetProperty("id").GetGuid();

        var responses = await Task.WhenAll(
            owner.Client.PostAsync(Url(household, $"/{id}/save")),
            owner.Client.PostAsync(Url(household, $"/{id}/save")));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Recipes WHERE HouseholdId = @h AND Title = @t", ("@h", household), ("@t", title)));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }
}
