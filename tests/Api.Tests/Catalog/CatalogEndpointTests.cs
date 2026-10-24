using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Catalog;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Catalog;

public sealed class CatalogEndpointTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AuthHost Host() => HouseholdHost.Create(db.ConnectionString);

    private static string NewName(string prefix = "Surdeig") => $"{prefix} {Guid.NewGuid():N}"[..40];

    private static async Task<List<JsonElement>> ListAsync(ApiClient client, Guid household, string query = "")
    {
        using var response = await client.GetAsync($"/api/households/{household}/ingredients{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.JsonAsync()).EnumerateArray()];
    }

    private static Task<HttpResponseMessage> CreateAsync(ApiClient client, Guid household, object body) =>
        client.PostAsync($"/api/households/{household}/ingredients", body);

    [Fact]
    public async Task Every_household_sees_the_global_catalog()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        var householdB = await b.Client.CreateHouseholdAsync();

        foreach (var (client, household) in new[] { (a.Client, householdA), (b.Client, householdB) })
        {
            var salt = Assert.Single(await ListAsync(client, household, "?search=salt"), e => e.GetProperty("name").GetString() == "Salt");
            Assert.True(salt.GetProperty("isGlobal").GetBoolean());
            Assert.True(salt.GetProperty("isStaple").GetBoolean());
            Assert.Equal("Mass", salt.GetProperty("dimension").GetString());
            Assert.Equal(IngredientSeed.All.Single(r => r.Key == "salt").Id, salt.GetProperty("id").GetGuid());
        }

        var all = await ListAsync(a.Client, householdA, "?limit=100");
        Assert.Equal(100, all.Count); // capped; the catalog has more
    }

    [Fact]
    public async Task A_household_ingredient_is_invisible_to_other_households_and_does_not_change_the_catalog()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        var householdB = await b.Client.CreateHouseholdAsync();
        var name = NewName();
        var globalsBefore = await CountAsync("HouseholdId IS NULL");

        using var created = await CreateAsync(a.Client, householdA, new { name, isStaple = true, dimension = "Mass" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.JsonAsync();
        Assert.Equal(name, body.GetProperty("name").GetString());
        Assert.False(body.GetProperty("isGlobal").GetBoolean());
        Assert.True(body.GetProperty("isStaple").GetBoolean());
        var id = body.GetProperty("id").GetGuid();

        var seenByA = Assert.Single(await ListAsync(a.Client, householdA, $"?search={Uri.EscapeDataString(name)}"));
        Assert.Equal(id, seenByA.GetProperty("id").GetGuid());
        Assert.Empty(await ListAsync(b.Client, householdB, $"?search={Uri.EscapeDataString(name)}"));
        Assert.Equal(globalsBefore, await CountAsync("HouseholdId IS NULL"));
        Assert.Equal(1, await CountAsync("HouseholdId = @h", ("@h", householdA)));

        // B can use the same name for its own ingredient: names are unique per household.
        using var createdByB = await CreateAsync(b.Client, householdB, new { name, dimension = "Count" });
        Assert.Equal(HttpStatusCode.Created, createdByB.StatusCode);
        var idB = (await createdByB.JsonAsync()).GetProperty("id").GetGuid();
        Assert.NotEqual(id, idB);
        Assert.Equal(idB, Assert.Single(await ListAsync(b.Client, householdB, $"?search={Uri.EscapeDataString(name)}")).GetProperty("id").GetGuid());
        Assert.Equal(id, Assert.Single(await ListAsync(a.Client, householdA, $"?search={Uri.EscapeDataString(name)}")).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Another_households_route_is_forbidden_and_leaks_nothing()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        await b.Client.CreateHouseholdAsync();
        var name = NewName();
        (await CreateAsync(a.Client, householdA, new { name, dimension = "Mass" })).Dispose();

        using var list = await b.Client.GetAsync($"/api/households/{householdA}/ingredients?search={Uri.EscapeDataString(name)}");
        await list.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var create = await CreateAsync(b.Client, householdA, new { name = NewName(), dimension = "Mass" });
        await create.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(0, await CountAsync("Name LIKE 'Surdeig%' AND HouseholdId = @h AND Name <> @n", ("@h", householdA), ("@n", name)));

        using var anonymous = host.CreateClient();
        using var denied = await anonymous.GetAsync($"/api/households/{householdA}/ingredients");
        await denied.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Child_can_read_but_not_create_and_adult_can_create()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        var adult = await host.JoinAsync(owner, household, "Adult");
        var name = NewName();

        using var byChild = await CreateAsync(child.Client, household, new { name, dimension = "Mass" });
        await byChild.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(0, await CountAsync("HouseholdId = @h", ("@h", household)));

        using var byAdult = await CreateAsync(adult.Client, household, new { name, dimension = "Mass" });
        Assert.Equal(HttpStatusCode.Created, byAdult.StatusCode);
        Assert.Single(await ListAsync(child.Client, household, $"?search={Uri.EscapeDataString(name)}"));
        using var byOwner = await CreateAsync(owner.Client, household, new { name = NewName(), dimension = "Volume" });
        Assert.Equal(HttpStatusCode.Created, byOwner.StatusCode);
    }

    [Fact]
    public async Task Duplicate_names_are_refused_against_the_household_and_the_global_catalog_ignoring_case()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var household = await a.Client.CreateHouseholdAsync();
        var name = NewName();
        (await CreateAsync(a.Client, household, new { name, dimension = "Mass" })).Dispose();

        using var own = await CreateAsync(a.Client, household, new { name = "  " + name.ToUpperInvariant() + " ", dimension = "Mass" });
        await own.AssertProblemAsync(HttpStatusCode.Conflict, "ingredient-exists");

        using var global = await CreateAsync(a.Client, household, new { name = "salt", dimension = "Mass" });
        await global.AssertProblemAsync(HttpStatusCode.Conflict, "ingredient-exists");
        using var globalWithAccent = await CreateAsync(a.Client, household, new { name = "RØMME", dimension = "Volume" });
        await globalWithAccent.AssertProblemAsync(HttpStatusCode.Conflict, "ingredient-exists");

        Assert.Equal(1, await CountAsync("HouseholdId = @h", ("@h", household)));
    }

    [Fact]
    public async Task Validation_is_problem_details()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var household = await a.Client.CreateHouseholdAsync();

        object[] bad =
        [
            new { name = "", dimension = "Mass" },
            new { name = "   ", dimension = "Mass" },
            new { name = new string('x', Ingredient.MaxNameLength + 1), dimension = "Mass" },
            new { name = "ok", dimension = "Weight" },
            new { name = "ok", dimension = "1" },
            new { name = "ok" },
            new { },
        ];
        foreach (var body in bad)
        {
            using var response = await CreateAsync(a.Client, household, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.True((await response.JsonAsync()).TryGetProperty("errors", out _));
        }

        using var boundary = await CreateAsync(a.Client, household, new { name = new string('y', Ingredient.MaxNameLength), dimension = "count" });
        Assert.Equal(HttpStatusCode.Created, boundary.StatusCode);

        foreach (var query in new[] { "?limit=0", "?limit=101", $"?search={new string('x', 101)}" })
        {
            using var response = await a.Client.GetAsync($"/api/households/{household}/ingredients{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Search_is_case_insensitive_and_limit_caps_the_result()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var household = await a.Client.CreateHouseholdAsync();

        var hits = await ListAsync(a.Client, household, "?search=OLJE");
        Assert.Contains(hits, e => e.GetProperty("name").GetString() == "Olivenolje");
        Assert.Contains(hits, e => e.GetProperty("name").GetString() == "Rapsolje");
        Assert.Equal(2, (await ListAsync(a.Client, household, "?limit=2")).Count);
        Assert.Equal(CatalogDefaults.Default, (await ListAsync(a.Client, household)).Count);
        Assert.Empty(await ListAsync(a.Client, household, "?search=finnesikke-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task Units_are_global_and_need_sign_in()
    {
        await using var host = Host();
        var user = await host.SignInAsync();

        using var response = await user.Client.GetAsync("/api/units");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var units = (await response.JsonAsync()).EnumerateArray().ToList();
        Assert.Equal(UnitSeed.All.Select(u => u.Code).Order(), units.Select(u => u.GetProperty("code").GetString()!).Order());
        var ss = units.Single(u => u.GetProperty("code").GetString() == "ss");
        Assert.Equal("Volume", ss.GetProperty("dimension").GetString());
        Assert.Equal(15m, ss.GetProperty("factorToBase").GetDecimal());

        using var anonymous = host.CreateClient();
        using var denied = await anonymous.GetAsync("/api/units");
        await denied.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task There_is_no_endpoint_that_can_change_or_delete_an_ingredient()
    {
        await using var host = Host();

        var methods = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.Contains("/ingredients", StringComparison.Ordinal) || e.RoutePattern.RawText.Contains("/units", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(m => $"{m} {e.RoutePattern.RawText!.TrimEnd('/')}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["GET /api/households/{householdId:guid}/ingredients", "GET /api/units", "POST /api/households/{householdId:guid}/ingredients"],
            methods);
    }

    [Fact]
    public async Task The_global_rows_are_still_untouched_after_all_of_this()
    {
        await using var context = TenancyDatabase_Production(db.ConnectionString);

        var globals = await context.Ingredients.AsNoTracking().Select(i => i.Id).ToListAsync(Ct);

        Assert.Equal(IngredientSeed.All.Select(r => r.Id).Order(), globals.Order()); // exactly the seed: no test wrote a global row
    }

    private static AppDbContext TenancyDatabase_Production(string connectionString) => Foodplanner.Api.Tests.Persistence.TenancyDatabase.CreateProductionContext(connectionString);

    private async Task<object?> CountAsync(string where, params (string Name, object Value)[] parameters) =>
        await HouseholdScenario.ScalarAsync(db.ConnectionString, $"SELECT COUNT(*) FROM Ingredients WHERE {where}", parameters);

    private static class CatalogDefaults
    {
        public const int Default = Foodplanner.Api.Features.Catalog.CatalogEndpoints.DefaultLimit;
    }
}
