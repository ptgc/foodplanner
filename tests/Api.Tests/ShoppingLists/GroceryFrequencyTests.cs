using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Domain.Catalog;

namespace Foodplanner.Api.Tests.ShoppingLists;

/// <summary>T-109: regular groceries with a frequency, the button that adds only what is due, and "add anyway".</summary>
public sealed class GroceryFrequencyTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    // Wednesday 2026-11-04: the current week is Monday 2026-11-02. 2 Nov is the first Monday of November (monthly due).
    private static readonly DateTimeOffset Wednesday = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
    private const string ThisWeek = "2026-11-02";
    private const string NextWeek = "2026-11-09";
    private static readonly Guid Milk = Ingredient("melk");
    private static readonly Guid Bread = Ingredient("brod");
    private static readonly Guid Cheese = Ingredient("ost");

    private static Guid Ingredient(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    private AuthHost Host()
    {
        var host = HouseholdHost.Create(db.ConnectionString);
        host.DefaultLanguage = "nb";
        host.Time.Now = Wednesday;
        return host;
    }

    private static string Url(Guid household, string rest = "") => $"/api/households/{household}/weekly-groceries{rest}";

    private static string ListUrl(Guid household, string week, string rest = "") =>
        $"/api/households/{household}/shopping-lists/{week}{rest}";

    private static async Task<JsonElement> AddAsync(
        ApiClient client, Guid household, Guid ingredient, string? frequency = null, string? nextDue = null)
    {
        using var response = await client.PostAsync(Url(household), new { ingredientId = ingredient, frequency, nextDue });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.JsonAsync();
    }

    private static async Task<JsonElement> PutAsync(ApiClient client, Guid household, JsonElement grocery, object body)
    {
        using var response = await client.PutAsync(Url(household, $"/{grocery.GetProperty("id").GetGuid()}"), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static async Task<JsonElement> PressAsync(ApiClient client, Guid household, string week)
    {
        using var response = await client.PostAsync(ListUrl(household, week, "/weekly"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static async Task<JsonElement> AddAnywayAsync(ApiClient client, Guid household, string week, JsonElement grocery)
    {
        using var response = await client.PostAsync(ListUrl(household, week, $"/weekly/{grocery.GetProperty("id").GetGuid()}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static string[] Names(JsonElement list) =>
        [.. list.GetProperty("main").EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];

    private static async Task AssertValidationAsync(HttpResponseMessage response, string key)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty(key, out _), $"expected an error for '{key}'");
    }

    private static string Text(JsonElement element, string property) => element.GetProperty(property).GetString()!;

    [Fact]
    public async Task A_grocery_is_weekly_by_default_and_is_due_this_week()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        var bread = await AddAsync(owner.Client, household, Bread);

        Assert.Equal("Weekly", Text(bread, "frequency"));
        Assert.Equal(JsonValueKind.Null, bread.GetProperty("startWeek").ValueKind);
        Assert.Equal(ThisWeek, Text(bread, "nextDueWeek"));
        Assert.True(bread.GetProperty("dueInWeek").GetBoolean());
    }

    [Fact]
    public async Task Every_second_week_starts_this_week_by_default_or_next_week_when_asked()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        var cheese = await AddAsync(owner.Client, household, Cheese, "EveryTwoWeeks");
        var milk = await AddAsync(owner.Client, household, Milk, "everytwoweeks", "NextWeek"); // names are case-insensitive

        Assert.Equal("EveryTwoWeeks", Text(cheese, "frequency"));
        Assert.Equal(ThisWeek, Text(cheese, "startWeek"));
        Assert.Equal(ThisWeek, Text(cheese, "nextDueWeek"));
        Assert.True(cheese.GetProperty("dueInWeek").GetBoolean());
        Assert.Equal("EveryTwoWeeks", Text(milk, "frequency"));
        Assert.Equal(NextWeek, Text(milk, "startWeek"));
        Assert.Equal(NextWeek, Text(milk, "nextDueWeek"));
        Assert.False(milk.GetProperty("dueInWeek").GetBoolean());
    }

    [Fact]
    public async Task Monthly_has_no_anchor_and_its_next_due_week_is_the_first_week_of_the_next_month_when_this_one_is_past()
    {
        await using var host = Host();
        host.Time.Now = new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero); // week of 9 Nov, past November's due week
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        var milk = await AddAsync(owner.Client, household, Milk, "Monthly");

        Assert.Equal(JsonValueKind.Null, milk.GetProperty("startWeek").ValueKind);
        Assert.Equal("2026-12-07", Text(milk, "nextDueWeek"));
        Assert.False(milk.GetProperty("dueInWeek").GetBoolean());
    }

    [Fact]
    public async Task This_week_is_the_week_in_oslo()
    {
        await using var host = Host();
        host.Time.Now = new DateTimeOffset(2026, 11, 8, 23, 30, 0, TimeSpan.Zero); // Sunday 23:30 UTC = Monday 00:30 in Oslo
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        var cheese = await AddAsync(owner.Client, household, Cheese, "EveryTwoWeeks");

        Assert.Equal(NextWeek, Text(cheese, "startWeek"));
    }

    [Fact]
    public async Task Bad_frequency_and_next_due_values_are_a_400()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var unknown = await owner.Client.PostAsync(Url(household), new { ingredientId = Milk, frequency = "Daily" });
        using var number = await owner.Client.PostAsync(Url(household), new { ingredientId = Milk, frequency = "1" });
        using var badNextDue = await owner.Client.PostAsync(Url(household), new { ingredientId = Milk, frequency = "EveryTwoWeeks", nextDue = "Tomorrow" });
        using var weeklyNextDue = await owner.Client.PostAsync(Url(household), new { ingredientId = Milk, nextDue = "NextWeek" });
        using var monthlyNextDue = await owner.Client.PostAsync(Url(household), new { ingredientId = Milk, frequency = "Monthly", nextDue = "ThisWeek" });
        await AssertValidationAsync(unknown, "frequency");
        await AssertValidationAsync(number, "frequency");
        await AssertValidationAsync(badNextDue, "nextDue");
        await AssertValidationAsync(weeklyNextDue, "nextDue");
        await AssertValidationAsync(monthlyNextDue, "nextDue");

        var bread = await AddAsync(owner.Client, household, Bread);
        var id = bread.GetProperty("id").GetGuid();
        using var putUnknown = await owner.Client.PutAsync(Url(household, $"/{id}"), new { frequency = "Daily" });
        using var putNextDue = await owner.Client.PutAsync(Url(household, $"/{id}"), new { nextDue = "NextWeek" }); // still Weekly
        await AssertValidationAsync(putUnknown, "frequency");
        await AssertValidationAsync(putNextDue, "nextDue");
        using var list = await owner.Client.GetAsync(Url(household));
        Assert.Equal(1, (await list.JsonAsync()).GetArrayLength()); // nothing was saved by the refused calls
    }

    [Fact]
    public async Task Changing_the_frequency_resets_the_anchor_and_editing_the_amount_keeps_the_schedule()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var cheese = await AddAsync(owner.Client, household, Cheese, "EveryTwoWeeks", "NextWeek");

        // Same frequency, no nextDue: the anchor stays. A quantity-only edit leaves frequency and anchor alone.
        var sameSchedule = await PutAsync(owner.Client, household, cheese, new { frequency = "EveryTwoWeeks" });
        var quantityOnly = await PutAsync(owner.Client, household, cheese, new { quantity = (decimal?)null, unitId = (Guid?)null });
        Assert.Equal(NextWeek, Text(sameSchedule, "startWeek"));
        Assert.Equal("EveryTwoWeeks", Text(quantityOnly, "frequency"));
        Assert.Equal(NextWeek, Text(quantityOnly, "startWeek"));

        // Moved to this week.
        var moved = await PutAsync(owner.Client, household, cheese, new { frequency = "EveryTwoWeeks", nextDue = "ThisWeek" });
        Assert.Equal(ThisWeek, Text(moved, "startWeek"));
        var movedNoFrequency = await PutAsync(owner.Client, household, cheese, new { nextDue = "NextWeek" }); // frequency stays
        Assert.Equal(NextWeek, Text(movedNoFrequency, "startWeek"));

        // A different frequency drops the anchor; back to every second week starts anew (this week by default).
        var monthly = await PutAsync(owner.Client, household, cheese, new { frequency = "Monthly" });
        Assert.Equal("Monthly", Text(monthly, "frequency"));
        Assert.Equal(JsonValueKind.Null, monthly.GetProperty("startWeek").ValueKind);
        var again = await PutAsync(owner.Client, household, cheese, new { frequency = "EveryTwoWeeks" });
        Assert.Equal(ThisWeek, Text(again, "startWeek"));
        var weekly = await PutAsync(owner.Client, household, cheese, new { frequency = "Weekly" });
        Assert.Equal(JsonValueKind.Null, weekly.GetProperty("startWeek").ValueKind);

        using var list = await owner.Client.GetAsync(Url(household));
        Assert.Equal("Weekly", Text((await list.JsonAsync())[0], "frequency"));
    }

    [Fact]
    public async Task The_list_says_which_groceries_are_due_in_the_week_asked_for()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await AddAsync(owner.Client, household, Bread);
        await AddAsync(owner.Client, household, Milk, "Monthly");
        await AddAsync(owner.Client, household, Cheese, "EveryTwoWeeks"); // anchor 2 Nov

        async Task<Dictionary<string, bool>> DueAsync(string? week)
        {
            using var response = await owner.Client.GetAsync(Url(household, week is null ? "" : $"?week={week}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.JsonAsync()).EnumerateArray().ToDictionary(g => Text(g, "name"), g => g.GetProperty("dueInWeek").GetBoolean());
        }

        Assert.Equal(new Dictionary<string, bool> { ["Brød"] = true, ["Melk"] = true, ["Ost"] = true }, await DueAsync(null));
        Assert.Equal(new Dictionary<string, bool> { ["Brød"] = true, ["Melk"] = false, ["Ost"] = false }, await DueAsync("2026-11-09"));
        Assert.Equal(new Dictionary<string, bool> { ["Brød"] = true, ["Melk"] = false, ["Ost"] = true }, await DueAsync("2026-11-16"));
        Assert.Equal(new Dictionary<string, bool> { ["Brød"] = true, ["Melk"] = true, ["Ost"] = false }, await DueAsync("2026-12-07"));
        Assert.Equal(new Dictionary<string, bool> { ["Brød"] = true, ["Melk"] = false, ["Ost"] = false }, await DueAsync("2026-10-26")); // before the anchor

        using var notMonday = await owner.Client.GetAsync(Url(household, "?week=2026-11-04"));
        using var garbage = await owner.Client.GetAsync(Url(household, "?week=soon"));
        await AssertValidationAsync(notMonday, "week");
        await AssertValidationAsync(garbage, "week");
    }

    [Fact]
    public async Task The_button_adds_only_what_is_due_and_pressing_again_changes_nothing()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await AddAsync(owner.Client, household, Bread);
        await AddAsync(owner.Client, household, Milk, "Monthly");
        await AddAsync(owner.Client, household, Cheese, "EveryTwoWeeks"); // anchor 2 Nov

        var next = await PressAsync(owner.Client, household, NextWeek);
        Assert.Equal(["Brød"], Names(next)); // milk is monthly (next due 7 Dec), cheese is due 2 Nov, 16 Nov

        var nextAgain = await PressAsync(owner.Client, household, NextWeek);
        Assert.Equal(next.GetRawText(), nextAgain.GetRawText());

        var first = await PressAsync(owner.Client, household, ThisWeek);
        Assert.Equal(["Ost", "Melk", "Brød"], Names(first)); // all three are due on 2 Nov
        var again = await PressAsync(owner.Client, household, ThisWeek);
        Assert.Equal(first.GetRawText(), again.GetRawText());
    }

    [Fact]
    public async Task A_set_with_nothing_due_changes_nothing_and_creates_no_list()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await AddAsync(owner.Client, household, Milk, "Monthly");

        var list = await PressAsync(owner.Client, household, NextWeek);

        Assert.Equal(JsonValueKind.Null, list.GetProperty("id").ValueKind);
        Assert.Empty(Names(list));
        Assert.Equal(0, (int)(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM ShoppingLists WHERE HouseholdId = @h", ("@h", household)))!);
    }

    [Fact]
    public async Task Add_anyway_puts_one_grocery_on_the_week_is_idempotent_and_survives_the_button_and_regeneration()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await AddAsync(owner.Client, household, Bread);
        var milk = await AddAsync(owner.Client, household, Milk, "Monthly");

        var first = await AddAnywayAsync(owner.Client, household, NextWeek, milk);
        Assert.Equal(["Melk"], Names(first)); // only the one asked for, not the due bread
        Assert.True(first.GetProperty("main")[0].GetProperty("isWeekly").GetBoolean());
        var second = await AddAnywayAsync(owner.Client, household, NextWeek, milk);
        Assert.Equal(first.GetRawText(), second.GetRawText());

        // The button adds what is due and does not take the not-due part away; regeneration keeps it too.
        Assert.Equal(["Melk", "Brød"], Names(await PressAsync(owner.Client, household, NextWeek)));
        using var regenerate = await owner.Client.PostAsync(ListUrl(household, NextWeek, "/generate"));
        Assert.Equal(["Melk", "Brød"], Names(await regenerate.JsonAsync()));
        Assert.Equal(2, (int)(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM ShoppingListItems WHERE HouseholdId = @h", ("@h", household)))!);
    }

    [Fact]
    public async Task A_weekly_part_added_before_the_frequency_changed_stays_on_the_list()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var bread = await AddAsync(owner.Client, household, Bread);
        await AddAsync(owner.Client, household, Cheese);
        await PressAsync(owner.Client, household, NextWeek);

        await PutAsync(owner.Client, household, bread, new { frequency = "Monthly" }); // not due on 9 Nov
        var pressed = await PressAsync(owner.Client, household, NextWeek);

        Assert.Equal(["Ost", "Brød"], Names(pressed));
    }

    [Fact]
    public async Task Add_anyway_works_for_any_member_and_refuses_an_unknown_or_foreign_grocery()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        var milk = await AddAsync(owner.Client, household, Milk, "Monthly");

        var byChild = await AddAnywayAsync(child.Client, household, NextWeek, milk);
        Assert.Equal(["Melk"], Names(byChild));

        var other = await host.SignInAsync();
        var otherHousehold = await other.Client.CreateHouseholdAsync();
        var foreign = await AddAsync(other.Client, otherHousehold, Cheese, "Monthly");
        using var foreignId = await owner.Client.PostAsync(ListUrl(household, NextWeek, $"/weekly/{foreign.GetProperty("id").GetGuid()}"));
        using var unknownId = await owner.Client.PostAsync(ListUrl(household, NextWeek, $"/weekly/{Guid.NewGuid()}"));
        using var stranger = await other.Client.PostAsync(ListUrl(household, NextWeek, $"/weekly/{milk.GetProperty("id").GetGuid()}"));
        Assert.Equal(HttpStatusCode.NotFound, foreignId.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownId.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
        using var read = await owner.Client.GetAsync(ListUrl(household, NextWeek));
        Assert.Equal(["Melk"], Names(await read.JsonAsync()));

        using var notMonday = await owner.Client.PostAsync(ListUrl(household, "2026-11-10", $"/weekly/{milk.GetProperty("id").GetGuid()}"));
        await AssertValidationAsync(notMonday, "weekStart");
    }

    [Fact]
    public async Task A_404_on_add_anyway_creates_no_list()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var response = await owner.Client.PostAsync(ListUrl(household, NextWeek, $"/weekly/{Guid.NewGuid()}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, (int)(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM ShoppingLists WHERE HouseholdId = @h", ("@h", household)))!);
    }
}
