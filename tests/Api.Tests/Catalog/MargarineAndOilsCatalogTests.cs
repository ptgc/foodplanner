using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Domain.Catalog;

namespace Foodplanner.Api.Tests.Catalog;

/// <summary>
/// T-084: the global catalog gained Margarin, Olje and Nøytral olje. The importer matches them, a household can no longer
/// create an own ingredient with those names (409), and own rows that already carry such a name (created before the seed)
/// keep working until the household merges them into the global rows.
/// </summary>
public sealed class MargarineAndOilsCatalogTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly Guid Margarine = Seed("margarin");
    private static readonly Guid Oil = Seed("olje");
    private static readonly Guid NeutralOil = Seed("noytral_olje");
    private static readonly Guid Butter = Seed("smor");

    private static Guid Seed(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    private static string Base(Guid household) => $"/api/households/{household}";

    private AuthHost Host()
    {
        var host = HouseholdHost.Create(db.ConnectionString);
        host.DefaultLanguage = "nb";
        return host;
    }

    private static async Task<JsonElement> ImportAsync(ApiClient client, Guid household, string ingredientLines)
    {
        using var response = await client.PostAsync(
            $"{Base(household)}/imports", new { text = $"Stekt noe\n\nIngredienser\n{ingredientLines}\n\nFremgangsmåte\nStek." });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.JsonAsync();
    }

    /// <summary>An own row with a name the global catalog now also has, as a household would have from before the seed (the API refuses to create one).</summary>
    private async Task<Guid> InsertOwnAsync(Guid household, string name, string dimension)
    {
        var id = Guid.NewGuid();
        await HouseholdScenario.ScalarAsync(
            db.ConnectionString,
            "INSERT INTO Ingredients (Id, HouseholdId, Name, IsStaple, Dimension, CreatedAt, UpdatedAt) VALUES (@id, @h, @n, 0, @d, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())",
            ("@id", id), ("@h", household), ("@n", name), ("@d", dimension));
        return id;
    }

    [Fact]
    public async Task The_new_rows_are_global_staples_with_nb_and_en_names()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        foreach (var (language, id, name, dimension) in new[]
        {
            ("nb", Margarine, "Margarin", "Mass"), ("en", Margarine, "Margarine", "Mass"),
            ("nb", Oil, "Olje", "Volume"), ("en", Oil, "Oil", "Volume"),
            ("nb", NeutralOil, "Nøytral olje", "Volume"), ("en", NeutralOil, "Neutral oil", "Volume"),
        })
        {
            owner.Client.Language = language;
            using var response = await owner.Client.GetAsync($"{Base(household)}/ingredients?search={Uri.EscapeDataString(name)}");
            var row = (await response.JsonAsync()).EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id);
            Assert.Equal(name, row.GetProperty("name").GetString());
            Assert.True(row.GetProperty("isGlobal").GetBoolean());
            Assert.True(row.GetProperty("isStaple").GetBoolean());
            Assert.Equal(dimension, row.GetProperty("dimension").GetString());
        }
    }

    [Theory]
    [InlineData("Margarin")]
    [InlineData("margarine")]
    [InlineData("OLJE")]
    [InlineData("Oil")]
    [InlineData("nøytral olje")]
    [InlineData("Neutral oil")]
    public async Task A_household_cannot_create_an_own_ingredient_with_a_new_global_name(string name)
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var response = await owner.Client.PostAsync($"{Base(household)}/ingredients", new { name, dimension = "Volume" });

        await response.AssertProblemAsync(HttpStatusCode.Conflict, "ingredient-exists");
    }

    [Fact]
    public async Task The_importer_matches_margarine_and_oils_also_as_alternatives()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        var draft = await ImportAsync(owner.Client, household, """
            margarin til steking
            2 ss olje
            nøytral olje
            1 ss oil
            200 g smør eller margarin
            olje til steking
            """);

        var lines = draft.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(6, lines.Count);
        Guid IdOf(int i) => lines[i].GetProperty("ingredientId").GetGuid();
        Assert.Equal([Margarine, Oil, NeutralOil, Oil, Butter, Oil], Enumerable.Range(0, 6).Select(IdOf));
        var alternatives = lines[4].GetProperty("alternatives").EnumerateArray().ToList();
        Assert.Equal(["Margarin"], alternatives.Select(a => a.GetProperty("ingredientName").GetString()));
        Assert.Equal(Margarine, alternatives[0].GetProperty("ingredientId").GetGuid());
        Assert.Equal("Frying", lines[5].GetProperty("purpose").GetString());
    }

    [Fact]
    public async Task Existing_own_rows_with_the_same_names_keep_working_and_can_be_merged_into_the_global_rows()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var other = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var otherHousehold = await other.Client.CreateHouseholdAsync();
        var ownNeutral = await InsertOwnAsync(household, "Nøytral olje", "Volume");
        var ownMargarine = await InsertOwnAsync(household, "Margarin eller olje", "Mass");

        // Both are visible next to the global rows, and the household that has no such row matches the global one.
        using var own = await owner.Client.GetAsync($"{Base(household)}/ingredients/own");
        Assert.Equal(["Margarin eller olje", "Nøytral olje"], (await own.JsonAsync()).EnumerateArray().Select(e => e.GetProperty("name").GetString()!).Order());
        using var all = await owner.Client.GetAsync($"{Base(household)}/ingredients?search=n%C3%B8ytral");
        Assert.Equal(2, (await all.JsonAsync()).EnumerateArray().Count(r => r.GetProperty("name").GetString() == "Nøytral olje"));

        // At an equal score the household's own row wins (existing recipes and drafts behave as before).
        var mine = (await ImportAsync(owner.Client, household, "nøytral olje")).GetProperty("lines")[0];
        Assert.Equal(ownNeutral, mine.GetProperty("ingredientId").GetGuid());
        var theirs = (await ImportAsync(other.Client, otherHousehold, "nøytral olje")).GetProperty("lines")[0];
        Assert.Equal(NeutralOil, theirs.GetProperty("ingredientId").GetGuid());

        // The Ingredients screen merge moves the open draft onto the global rows; afterwards the import matches the global row.
        using var mergedNeutral = await owner.Client.PostAsync($"{Base(household)}/ingredients/{ownNeutral}/merge", new { targetIngredientId = NeutralOil });
        Assert.Equal(HttpStatusCode.OK, mergedNeutral.StatusCode);
        using var mergedMargarine = await owner.Client.PostAsync($"{Base(household)}/ingredients/{ownMargarine}/merge", new { targetIngredientId = Margarine });
        Assert.Equal(HttpStatusCode.OK, mergedMargarine.StatusCode);
        var after = (await ImportAsync(owner.Client, household, "nøytral olje")).GetProperty("lines")[0];
        Assert.Equal(NeutralOil, after.GetProperty("ingredientId").GetGuid());
        using var rest = await owner.Client.GetAsync($"{Base(household)}/ingredients/own");
        Assert.Empty((await rest.JsonAsync()).EnumerateArray());
    }
}
