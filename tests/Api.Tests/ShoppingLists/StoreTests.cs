using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Catalog;

namespace Foodplanner.Api.Tests.ShoppingLists;

/// <summary>T-087: the household's stores with a section order, the chosen store per user, and the list ordered by it.</summary>
public sealed class StoreTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly Guid Milk = Ingredient("melk"); // Dairy
    private static readonly Guid Bread = Ingredient("brod"); // DryGoods
    private static readonly Guid Cheese = Ingredient("ost"); // MeatFishAndCheese

    private static readonly string[] DryFirst = ["DryGoods", "Dairy", "Frozen", "MeatFishAndCheese", "FruitAndVegetables", "Other"];
    private static readonly string[] DefaultOrder = ["FruitAndVegetables", "MeatFishAndCheese", "Frozen", "Dairy", "DryGoods", "Other"];

    private static Guid Ingredient(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    private AuthHost Host()
    {
        var host = HouseholdHost.Create(db.ConnectionString);
        host.DefaultLanguage = "nb";
        return host;
    }

    private static string Url(Guid household, string rest = "") => $"/api/households/{household}/stores{rest}";

    private static string ChosenUrl(Guid household) => $"/api/households/{household}/chosen-store";

    private static string ListUrl(Guid household, string rest = "") =>
        $"/api/households/{household}/shopping-lists/{Monday:yyyy-MM-dd}{rest}";

    private async Task AssertNoCrossHouseholdLinksAsync()
    {
        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }

    private static async Task<Guid> CreateAsync(ApiClient client, Guid household, string name, string[]? order = null)
    {
        using var response = await client.PostAsync(Url(household), new { name, sectionOrder = order });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task ChooseAsync(ApiClient client, Guid household, Guid? storeId)
    {
        using var response = await client.PutAsync(ChosenUrl(household), new { storeId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task PutWeeklyOnListAsync(ApiClient owner, Guid household)
    {
        foreach (var ingredient in new[] { Milk, Bread, Cheese })
        {
            using var add = await owner.PostAsync($"/api/households/{household}/weekly-groceries", new { ingredientId = ingredient });
            Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        }

        using var press = await owner.PostAsync(ListUrl(household, "/weekly"));
        Assert.Equal(HttpStatusCode.OK, press.StatusCode);
    }

    private static async Task<string[]> MainNamesAsync(ApiClient client, Guid household)
    {
        using var response = await client.GetAsync(ListUrl(household));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.JsonAsync()).GetProperty("main").EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];
    }

    private static async Task AssertValidationAsync(HttpResponseMessage response, string key)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty(key, out _), $"expected an error for '{key}'");
    }

    [Fact]
    public async Task An_adult_creates_renames_reorders_and_deletes_a_store()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        var id = await CreateAsync(owner.Client, household, "  Rema   Lade ");
        using var list = await owner.Client.GetAsync(Url(household));
        var store = (await list.JsonAsync()).EnumerateArray().Single();
        Assert.Equal("Rema Lade", store.GetProperty("name").GetString());
        Assert.Equal(DefaultOrder, store.GetProperty("sectionOrder").EnumerateArray().Select(s => s.GetString()!)); // left out = the fixed order

        using var update = await owner.Client.PutAsync(Url(household, $"/{id}"), new { name = "Kiwi sentrum", sectionOrder = DryFirst });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.JsonAsync();
        Assert.Equal("Kiwi sentrum", updated.GetProperty("name").GetString());
        Assert.Equal(DryFirst, updated.GetProperty("sectionOrder").EnumerateArray().Select(s => s.GetString()!));

        using var remove = await owner.Client.DeleteAsync(Url(household, $"/{id}"));
        using var removeAgain = await owner.Client.DeleteAsync(Url(household, $"/{id}"));
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, removeAgain.StatusCode);
        using var after = await owner.Client.GetAsync(Url(household));
        Assert.Equal(0, (await after.JsonAsync()).GetArrayLength());
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Name_and_section_order_are_validated_at_the_boundary()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var id = await CreateAsync(owner.Client, household, "Rema");

        using var noName = await owner.Client.PostAsync(Url(household), new { name = "  " });
        using var longName = await owner.Client.PostAsync(Url(household), new { name = new string('x', 101) });
        using var missingSection = await owner.Client.PostAsync(Url(household), new { name = "A", sectionOrder = DefaultOrder[..5] });
        using var duplicateSection = await owner.Client.PostAsync(Url(household), new { name = "B", sectionOrder = new[] { "Dairy", "Dairy", "Frozen", "MeatFishAndCheese", "FruitAndVegetables", "Other" } });
        using var unknownSection = await owner.Client.PostAsync(Url(household), new { name = "C", sectionOrder = new[] { "Bakery", "Dairy", "Frozen", "MeatFishAndCheese", "FruitAndVegetables", "Other" } });
        using var updateWithoutOrder = await owner.Client.PutAsync(Url(household, $"/{id}"), new { name = "Rema" });
        await AssertValidationAsync(noName, "name");
        await AssertValidationAsync(longName, "name");
        await AssertValidationAsync(missingSection, "sectionOrder");
        await AssertValidationAsync(duplicateSection, "sectionOrder");
        await AssertValidationAsync(unknownSection, "sectionOrder");
        await AssertValidationAsync(updateWithoutOrder, "sectionOrder");
        Assert.Contains("application/problem+json", noName.Content.Headers.ContentType?.ToString() ?? string.Empty, StringComparison.Ordinal);

        using var list = await owner.Client.GetAsync(Url(household));
        Assert.Equal(1, (await list.JsonAsync()).GetArrayLength());
    }

    [Fact]
    public async Task A_store_name_is_unique_per_household_ignoring_case_but_not_across_households()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var other = await host.SignInAsync();
        var otherHousehold = await other.Client.CreateHouseholdAsync();
        var id = await CreateAsync(owner.Client, household, "Rema Lade");
        var second = await CreateAsync(owner.Client, household, "Kiwi");

        using var duplicate = await owner.Client.PostAsync(Url(household), new { name = "rema lade" });
        using var rename = await owner.Client.PutAsync(Url(household, $"/{second}"), new { name = "REMA LADE", sectionOrder = DefaultOrder });
        using var sameNameAgain = await owner.Client.PutAsync(Url(household, $"/{id}"), new { name = "rema lade", sectionOrder = DefaultOrder });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.EndsWith("store-exists", (await duplicate.JsonAsync()).GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sameNameAgain.StatusCode); // its own name in another case is a rename, not a clash

        await CreateAsync(other.Client, otherHousehold, "Rema Lade"); // another household may use the name
    }

    [Fact]
    public async Task Members_read_and_choose_but_only_adults_manage_and_strangers_are_refused()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        var adult = await host.JoinAsync(owner, household, "Adult");
        var stranger = await host.SignInAsync();
        var id = await CreateAsync(owner.Client, household, "Rema");

        using var read = await child.Client.GetAsync(Url(household));
        using var add = await child.Client.PostAsync(Url(household), new { name = "Kiwi" });
        using var edit = await child.Client.PutAsync(Url(household, $"/{id}"), new { name = "Kiwi", sectionOrder = DefaultOrder });
        using var remove = await child.Client.DeleteAsync(Url(household, $"/{id}"));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        await ChooseAsync(child.Client, household, id); // picking is for every member
        using var adultAdd = await adult.Client.PostAsync(Url(household), new { name = "Kiwi" });
        Assert.Equal(HttpStatusCode.Created, adultAdd.StatusCode);

        using var strangerRead = await stranger.Client.GetAsync(Url(household));
        using var strangerAdd = await stranger.Client.PostAsync(Url(household), new { name = "Kiwi" });
        using var strangerChoose = await stranger.Client.PutAsync(ChosenUrl(household), new { storeId = id });
        using var strangerChosen = await stranger.Client.GetAsync(ChosenUrl(household));
        Assert.Equal(HttpStatusCode.Forbidden, strangerRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, strangerAdd.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, strangerChoose.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, strangerChosen.StatusCode);
    }

    [Fact]
    public async Task The_list_follows_the_chosen_store_per_user_and_falls_back_to_the_fixed_order()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        await PutWeeklyOnListAsync(owner.Client, household);
        var store = await CreateAsync(owner.Client, household, "Rema Lade", DryFirst);

        Assert.Equal(["Ost", "Melk", "Brød"], await MainNamesAsync(owner.Client, household)); // no choice: the fixed order

        await ChooseAsync(owner.Client, household, store);
        Assert.Equal(["Brød", "Melk", "Ost"], await MainNamesAsync(owner.Client, household));
        Assert.Equal(["Ost", "Melk", "Brød"], await MainNamesAsync(child.Client, household)); // the other member's order is their own

        using var chosen = await owner.Client.GetAsync(ChosenUrl(household));
        Assert.Equal(store, (await chosen.JsonAsync()).GetProperty("storeId").GetGuid()); // remembered on the server

        using var export = await owner.Client.GetAsync(ListUrl(household, "/export"));
        var text = (await export.JsonAsync()).GetProperty("text").GetString()!;
        Assert.True(text.IndexOf("Brød", StringComparison.Ordinal) < text.IndexOf("Melk", StringComparison.Ordinal), text);

        await ChooseAsync(owner.Client, household, null); // clearing falls back
        Assert.Equal(["Ost", "Melk", "Brød"], await MainNamesAsync(owner.Client, household));
    }

    [Fact]
    public async Task The_hub_state_is_ordered_for_the_connected_user_not_for_everyone()
    {
        // The change event carries no content; each client re-reads its own state, so a live update renders in the viewer's own order.
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        await PutWeeklyOnListAsync(owner.Client, household);
        await ChooseAsync(owner.Client, household, await CreateAsync(owner.Client, household, "Rema Lade", DryFirst));
        await using var ownerHub = new HubTestClient(host, owner.Client.AuthCookie);
        await using var childHub = new HubTestClient(host, child.Client.AuthCookie);
        await ownerHub.StartAsync();
        await childHub.StartAsync();

        static string[] Names(JsonElement state) =>
            [.. state.GetProperty("list").GetProperty("main").EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];

        var ownerState = await ownerHub.GetStateAsync(household, Monday, "nb");
        var childState = await childHub.GetStateAsync(household, Monday, "nb");
        Assert.Equal(["Brød", "Melk", "Ost"], Names(ownerState));
        Assert.Equal(["Ost", "Melk", "Brød"], Names(childState));
        Assert.NotEqual(ownerState.GetProperty("version").GetString(), childState.GetProperty("version").GetString()); // the fingerprint covers the order
    }

    [Fact]
    public async Task Deleting_a_household_whose_members_chose_a_store_leaves_nothing_behind()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        var store = await CreateAsync(owner.Client, household, "Rema Lade");
        await ChooseAsync(owner.Client, household, store);
        await ChooseAsync(child.Client, household, store);

        using var deleted = await owner.Client.DeleteAsync($"/api/households/{household}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        foreach (var table in new[] { "Stores", "Memberships", "Households" })
        {
            var column = table == "Households" ? "Id" : "HouseholdId";
            Assert.Equal(0, Convert.ToInt32(await HouseholdScenario.ScalarAsync(
                db.ConnectionString, $"SELECT COUNT(*) FROM {table} WHERE {column} = @h", ("@h", household))));
        }
    }

    [Fact]
    public async Task A_member_with_a_chosen_store_can_leave_and_be_removed()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var leaver = await host.JoinAsync(owner, household, "Adult");
        var removed = await host.JoinAsync(owner, household, "Child");
        var store = await CreateAsync(owner.Client, household, "Rema Lade");
        await ChooseAsync(leaver.Client, household, store);
        await ChooseAsync(removed.Client, household, store);

        using var left = await leaver.Client.PostAsync($"/api/households/{household}/leave");
        using var remove = await owner.Client.DeleteAsync($"/api/households/{household}/members/{removed.User.Id}");

        Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        using var stores = await owner.Client.GetAsync(Url(household));
        Assert.Equal(1, (await stores.JsonAsync()).GetArrayLength()); // the store itself stays
    }

    [Fact]
    public async Task Deleting_a_store_survives_members_choosing_it_at_the_same_time()
    {
        // Not deterministic by design: it races choices against the delete. Whatever the interleaving, the answer is 204 (or the
        // documented 409 store-conflict), never a 500, and no choice of a deleted store is left behind.
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var members = new List<SignedInUser>();
        for (var i = 0; i < 3; i++)
        {
            members.Add(await host.JoinAsync(owner, household, "Adult"));
        }

        var store = await CreateAsync(owner.Client, household, "Rema Lade");
        var choosing = members.Select(m => m.Client.PutAsync(ChosenUrl(household), new { storeId = store })).ToList();
        var deleting = owner.Client.DeleteAsync(Url(household, $"/{store}"));
        await Task.WhenAll([.. choosing, deleting]);

        using var delete = await deleting;
        Assert.Contains(delete.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict });
        foreach (var task in choosing)
        {
            using var response = await task;
            Assert.DoesNotContain(response.StatusCode, new[] { HttpStatusCode.InternalServerError });
        }

        using var again = await owner.Client.DeleteAsync(Url(household, $"/{store}"));
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(0, Convert.ToInt32(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM Memberships WHERE ChosenStoreId = @s", ("@s", store))));
    }

    [Fact]
    public async Task Deleting_a_store_clears_every_users_choice_of_it()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        await PutWeeklyOnListAsync(owner.Client, household);
        var store = await CreateAsync(owner.Client, household, "Rema Lade", DryFirst);
        await ChooseAsync(owner.Client, household, store);
        await ChooseAsync(child.Client, household, store);

        using var remove = await owner.Client.DeleteAsync(Url(household, $"/{store}"));
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);

        foreach (var member in new[] { owner.Client, child.Client })
        {
            using var chosen = await member.GetAsync(ChosenUrl(household));
            Assert.Equal(JsonValueKind.Null, (await chosen.JsonAsync()).GetProperty("storeId").ValueKind);
            Assert.Equal(["Ost", "Melk", "Brød"], await MainNamesAsync(member, household));
        }

        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Another_households_store_cannot_be_chosen_and_an_unknown_one_gets_the_same_answer()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var other = await host.SignInAsync();
        var otherHousehold = await other.Client.CreateHouseholdAsync();
        var foreign = await CreateAsync(other.Client, otherHousehold, "Hemmelig");

        using var foreignChoice = await owner.Client.PutAsync(ChosenUrl(household), new { storeId = foreign });
        using var unknownChoice = await owner.Client.PutAsync(ChosenUrl(household), new { storeId = Guid.NewGuid() });
        using var foreignUpdate = await owner.Client.PutAsync(Url(household, $"/{foreign}"), new { name = "X", sectionOrder = DefaultOrder });
        using var foreignDelete = await owner.Client.DeleteAsync(Url(household, $"/{foreign}"));
        await AssertValidationAsync(foreignChoice, "storeId");
        await AssertValidationAsync(unknownChoice, "storeId");
        Assert.Equal(HttpStatusCode.NotFound, foreignUpdate.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, foreignDelete.StatusCode); // idempotent, and nothing was removed:
        using var stillThere = await other.Client.GetAsync(Url(otherHousehold));
        Assert.Equal(1, (await stillThere.JsonAsync()).GetArrayLength());
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task A_household_has_at_most_the_limit_of_stores()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        for (var i = 0; i < Foodplanner.Domain.ShoppingLists.Store.MaxPerHousehold; i++)
        {
            await CreateAsync(owner.Client, household, $"Butikk {i}");
        }

        using var over = await owner.Client.PostAsync(Url(household), new { name = "En til" });
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.EndsWith("store-limit", (await over.JsonAsync()).GetProperty("type").GetString());
    }
}
