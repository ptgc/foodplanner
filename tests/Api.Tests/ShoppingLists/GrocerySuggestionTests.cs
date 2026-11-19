using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Tests.ShoppingLists;

/// <summary>T-110, Q-030: suggestions of regular groceries learnt from the items a household adds by hand.</summary>
public sealed class GrocerySuggestionTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly Guid Milk = IngredientSeed.All.Single(r => r.Key == "melk").Id; // Melk / Milk
    private static readonly Guid Cheese = IngredientSeed.All.Single(r => r.Key == "ost").Id; // Ost / Cheese

    private AuthHost Host()
    {
        var host = HouseholdHost.Create(db.ConnectionString);
        host.DefaultLanguage = "nb";
        // A fixed Wednesday noon (UTC) in a normal week, so "the current week" is the same in Oslo.
        host.Time.Now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        return host;
    }

    private static readonly DateOnly Current = new(2026, 11, 2);

    private static DateOnly Week(int offset) => Current.AddDays(7 * offset);

    private static string Url(Guid household, string rest = "") => $"/api/households/{household}/weekly-groceries{rest}";

    private static async Task AddByHandAsync(ApiClient client, Guid household, int weekOffset, string label)
    {
        using var response = await client.PostAsync(
            $"/api/households/{household}/shopping-lists/{Week(weekOffset):yyyy-MM-dd}/items", new { label });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task AddByHandAsync(ApiClient client, Guid household, string label, params int[] weekOffsets)
    {
        foreach (var offset in weekOffsets)
        {
            await AddByHandAsync(client, household, offset, label);
        }
    }

    private static async Task<JsonElement[]> SuggestionsAsync(ApiClient client, Guid household)
    {
        using var response = await client.GetAsync(Url(household, "/suggestions"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.JsonAsync()).EnumerateArray()];
    }

    private static string[] Labels(IEnumerable<JsonElement> rows) => [.. rows.Select(r => r.GetProperty("label").GetString()!)];

    private static async Task DismissAsync(ApiClient client, Guid household, string label)
    {
        using var response = await client.PostAsync(Url(household, "/suggestions/dismiss"), new { label });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Items_added_by_hand_in_four_weeks_are_suggested_with_frequency_and_counts()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await AddByHandAsync(owner.Client, household, "Pålegg", -3, -2, -1, 0);
        await AddByHandAsync(owner.Client, household, "Frokostblanding", -8, -6, -4, -2, 0);
        await AddByHandAsync(owner.Client, household, "Sjeldent", -1, 0);

        var rows = await SuggestionsAsync(owner.Client, household);

        Assert.Equal(["Frokostblanding", "Pålegg"], Labels(rows)); // most weeks first
        var weekly = rows.Single(r => r.GetProperty("label").GetString() == "Pålegg");
        Assert.Equal("Weekly", weekly.GetProperty("frequency").GetString());
        Assert.Equal(4, weekly.GetProperty("weeksSeen").GetInt32());
        Assert.Equal(12, weekly.GetProperty("weeksLooked").GetInt32());
        var biweekly = rows.Single(r => r.GetProperty("label").GetString() == "Frokostblanding");
        Assert.Equal("EveryTwoWeeks", biweekly.GetProperty("frequency").GetString());
        Assert.Equal(5, biweekly.GetProperty("weeksSeen").GetInt32());
    }

    [Fact]
    public async Task Generated_items_and_weekly_parts_are_not_signal_only_hand_added_items_are()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        // Recipe-driven: milk is planned and generated in four weeks, so a generated item (ingredient, with a source) exists each week.
        using var recipe = await owner.Client.PostAsync($"/api/households/{household}/recipes", new
        {
            title = "Pannekaker",
            servings = 4,
            ingredients = new[] { new { ingredientId = Milk, quantity = (decimal?)5m, unitId = (Guid?)UnitSeed.All.Single(u => u.Code == "dl").Id, note = (string?)null } },
        });
        Assert.Equal(HttpStatusCode.Created, recipe.StatusCode);
        var recipeId = (await recipe.JsonAsync()).GetProperty("id").GetGuid();
        // A regular grocery for cheese puts a weekly part on the list in the same four weeks.
        using var grocery = await owner.Client.PostAsync(Url(household), new { ingredientId = Cheese });
        Assert.Equal(HttpStatusCode.Created, grocery.StatusCode);
        foreach (var offset in new[] { -3, -2, -1, 0 })
        {
            var monday = Week(offset).ToString("yyyy-MM-dd");
            using var entry = await owner.Client.PostAsync(
                $"/api/households/{household}/meal-plans/{monday}/entries", new { date = monday, recipeId, servings = 4 });
            Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
            using var generate = await owner.Client.PostAsync($"/api/households/{household}/shopping-lists/{monday}/generate");
            Assert.Equal(HttpStatusCode.OK, generate.StatusCode);
            using var weekly = await owner.Client.PostAsync($"/api/households/{household}/shopping-lists/{monday}/weekly");
            Assert.Equal(HttpStatusCode.OK, weekly.StatusCode);
        }

        Assert.Empty(await SuggestionsAsync(owner.Client, household));

        // The same milk typed by hand in the same weeks IS signal: a manual item is a label of its own, never a recipe item.
        await AddByHandAsync(owner.Client, household, "Melk", -3, -2, -1, 0);
        var rows = await SuggestionsAsync(owner.Client, household);
        Assert.Equal(["Melk"], Labels(rows));
        Assert.Equal(4, rows[0].GetProperty("weeksSeen").GetInt32());
    }

    [Fact]
    public async Task Accepting_creates_a_regular_grocery_and_the_label_is_no_longer_suggested_in_any_language_or_plural()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await AddByHandAsync(owner.Client, household, "oster", -3, -2, -1, 0);
        await AddByHandAsync(owner.Client, household, "Milk", -3, -2, -1, 0);
        Assert.Equal(2, (await SuggestionsAsync(owner.Client, household)).Length);

        // The client accepts through the existing endpoint: nothing is added by the suggestion itself.
        var accepted = await owner.Client.PostAsync(Url(household), new { ingredientId = Cheese, frequency = "Weekly" });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(["Milk"], Labels(await SuggestionsAsync(owner.Client, household))); // "oster" matches Ost (nb) by normalised name

        using var second = await owner.Client.PostAsync(Url(household), new { ingredientId = Milk, frequency = "EveryTwoWeeks" });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Empty(await SuggestionsAsync(owner.Client, household)); // "Milk" matches the English name, the nb request language notwithstanding
    }

    [Fact]
    public async Task A_dismissed_suggestion_is_hidden_for_twelve_weeks_and_comes_back_after_that_if_still_regular()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        for (var offset = -11; offset <= 0; offset++)
        {
            await AddByHandAsync(owner.Client, household, offset, "Pålegg");
        }

        Assert.Equal(["Pålegg"], Labels(await SuggestionsAsync(owner.Client, household)));
        await DismissAsync(owner.Client, household, "  PÅLEGG ");
        Assert.Empty(await SuggestionsAsync(owner.Client, household));

        // Moving the clock would expire the test session, so the dismissal is moved back in time instead.
        await BackdateDismissalsAsync(household, host.Time.Now.AddDays(-(7 * 12 - 1)));
        Assert.Empty(await SuggestionsAsync(owner.Client, household)); // still hidden just before 12 weeks

        await BackdateDismissalsAsync(household, host.Time.Now.AddDays(-(7 * 12 + 1)));
        Assert.Equal(["Pålegg"], Labels(await SuggestionsAsync(owner.Client, household))); // back after 12 weeks

        await DismissAsync(owner.Client, household, "Pålegg"); // dismissing again renews it and removes nothing else
        Assert.Empty(await SuggestionsAsync(owner.Client, household));
    }

    private async Task BackdateDismissalsAsync(Guid household, DateTimeOffset dismissedAt)
    {
        await using var context = TestDbContext.Create(db.ConnectionString, household);
        await context.DismissedGrocerySuggestions.ExecuteUpdateAsync(
            s => s.SetProperty(d => d.DismissedAt, dismissedAt), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Dismissing_is_idempotent_validated_and_keeps_one_row_per_normalised_label()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        await DismissAsync(owner.Client, household, "Oster");
        await DismissAsync(owner.Client, household, "ost");
        await DismissAsync(owner.Client, household, "Ost");
        await using (var context = TestDbContext.Create(db.ConnectionString, household))
        {
            Assert.Equal(1, context.DismissedGrocerySuggestions.Count());
        }

        foreach (var bad in new object?[] { new { label = (string?)null }, new { label = "  " }, new { label = "---" }, new { label = new string('a', 101) }, new { label = "a\u0007b" } })
        {
            using var response = await owner.Client.PostAsync(Url(household, "/suggestions/dismiss"), bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("label", out _));
        }

        using var noBody = await owner.Client.PostAsync(Url(household, "/suggestions/dismiss"));
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
    }

    [Fact]
    public async Task Dismissing_the_same_label_at_once_is_never_a_server_error()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var response = await owner.Client.PostAsync(Url(household, "/suggestions/dismiss"), new { label = "Parallell" });
            return response.StatusCode;
        }));

        Assert.All(responses, status => Assert.Equal(HttpStatusCode.NoContent, status));
        await using var context = TestDbContext.Create(db.ConnectionString, household);
        Assert.Equal(1, context.DismissedGrocerySuggestions.Count());
    }

    [Fact]
    public async Task Renewing_an_expired_dismissal_while_other_dismissals_clean_up_expired_rows_still_stores_it()
    {
        // The race itself (another request's cleanup deleting the expired row this one is renewing) cannot be forced from outside the
        // endpoint, so this hammers it: an expired row for the key, and parallel dismissals whose cleanup would delete it.
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        for (var round = 0; round < 4; round++)
        {
            await DismissAsync(owner.Client, household, "Pålegg");
            await BackdateDismissalsAsync(household, host.Time.Now.AddDays(-(7 * 13)));

            var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
            {
                using var response = await owner.Client.PostAsync(Url(household, "/suggestions/dismiss"), new { label = i % 2 == 0 ? "Pålegg" : "Annet " + round + i });
                return response.StatusCode;
            }));

            Assert.All(statuses, s => Assert.Equal(HttpStatusCode.NoContent, s));
            await using var context = TestDbContext.Create(db.ConnectionString, household);
            var stored = context.DismissedGrocerySuggestions.Single(d => d.Label == Foodplanner.Domain.ShoppingLists.GroceryLabel.Normalize("Pålegg"));
            Assert.True(stored.DismissedAt > host.Time.Now.AddDays(-1), "the renewed dismissal must be stored with the new time");
        }
    }

    [Fact]
    public async Task A_cleanup_delete_of_a_row_that_was_renewed_meanwhile_fails_instead_of_losing_the_renewal()
    {
        // T-112, the deterministic form of the race above: a cleanup read the expired row, a renewal then committed, and the
        // cleanup deletes. Without DismissedAt as concurrency token that DELETE succeeded and the renewed (204) dismissal was gone.
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await DismissAsync(owner.Client, household, "Pålegg");
        await BackdateDismissalsAsync(household, host.Time.Now.AddDays(-(7 * 13)));

        await using var cleanup = TestDbContext.Create(db.ConnectionString, household);
        var stale = await cleanup.DismissedGrocerySuggestions.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(stale);

        await DismissAsync(owner.Client, household, "Pålegg"); // the renewal commits first

        cleanup.DismissedGrocerySuggestions.RemoveRange(stale);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => cleanup.SaveChangesAsync(TestContext.Current.CancellationToken));

        await using var check = TestDbContext.Create(db.ConnectionString, household);
        var stored = await check.DismissedGrocerySuggestions.SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(stored.DismissedAt > host.Time.Now.AddDays(-1));
    }

    [Fact]
    public async Task Only_owner_and_adult_may_read_and_dismiss_a_child_and_a_stranger_are_refused()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");
        var stranger = await host.SignInAsync();

        Assert.Equal(HttpStatusCode.OK, (await adult.Client.GetAsync(Url(household, "/suggestions"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await adult.Client.PostAsync(Url(household, "/suggestions/dismiss"), new { label = "x" })).StatusCode);
        foreach (var other in new[] { child, stranger })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await other.Client.GetAsync(Url(household, "/suggestions"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.Client.PostAsync(Url(household, "/suggestions/dismiss"), new { label = "x" })).StatusCode);
        }
    }

    [Fact]
    public async Task The_suggestions_route_does_not_clash_with_the_grocery_id_routes()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        using var created = await owner.Client.PostAsync(Url(household), new { ingredientId = Milk });
        var id = (await created.JsonAsync()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await owner.Client.PutAsync(Url(household, $"/{id}"), new { quantity = (decimal?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync(Url(household, $"/{id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync(Url(household, "/suggestions"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync(Url(household))).StatusCode);
    }

    [Fact]
    public async Task Another_household_never_sees_or_affects_these_labels_or_dismissals()
    {
        await using var host = Host();
        var ownerA = await host.SignInAsync();
        var householdA = await ownerA.Client.CreateHouseholdAsync();
        var ownerB = await host.SignInAsync();
        var householdB = await ownerB.Client.CreateHouseholdAsync("Other");
        await AddByHandAsync(ownerA.Client, householdA, "Hemmelig pålegg", -3, -2, -1, 0);
        await AddByHandAsync(ownerB.Client, householdB, "Hemmelig pålegg", -3, -2, -1, 0);
        await AddByHandAsync(ownerB.Client, householdB, "Kun hos B", -3, -2, -1, 0);

        Assert.Equal(["Hemmelig pålegg"], Labels(await SuggestionsAsync(ownerA.Client, householdA)));

        await DismissAsync(ownerB.Client, householdB, "Hemmelig pålegg");
        await ownerB.Client.PostAsync(Url(householdB), new { ingredientId = Milk });
        Assert.Equal(["Kun hos B"], Labels(await SuggestionsAsync(ownerB.Client, householdB)));
        Assert.Equal(["Hemmelig pålegg"], Labels(await SuggestionsAsync(ownerA.Client, householdA))); // B's dismissal does not hide A's

        // A cannot use B's household id in the route.
        using var crossRead = await ownerA.Client.GetAsync(Url(householdB, "/suggestions"));
        using var crossDismiss = await ownerA.Client.PostAsync(Url(householdB, "/suggestions/dismiss"), new { label = "Kun hos B" });
        Assert.Equal(HttpStatusCode.Forbidden, crossRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossDismiss.StatusCode);
        Assert.Equal(["Kun hos B"], Labels(await SuggestionsAsync(ownerB.Client, householdB)));
    }

    [Fact]
    public async Task Only_the_twelve_weeks_ending_with_the_current_week_are_read()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await AddByHandAsync(owner.Client, household, "Gammelt", -16, -15, -14, -13, -12);
        await AddByHandAsync(owner.Client, household, "Fremtid", 1, 2, 3, 4);

        Assert.Empty(await SuggestionsAsync(owner.Client, household));
    }

    [Fact]
    public async Task The_limit_of_500_dismissals_answers_409_and_expired_ones_are_cleaned_when_someone_dismisses()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await using (var context = TestDbContext.Create(db.ConnectionString, household))
        {
            for (var i = 0; i < 500; i++)
            {
                context.DismissedGrocerySuggestions.Add(new Foodplanner.Domain.ShoppingLists.DismissedGrocerySuggestion($"label {i}", host.Time.Now));
            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var over = await owner.Client.PostAsync(Url(household, "/suggestions/dismiss"), new { label = "en til" });
        await over.AssertProblemAsync(HttpStatusCode.Conflict, "dismissed-suggestion-limit");
        await DismissAsync(owner.Client, household, "label 7"); // an existing one is renewed, not refused

        // Expire them all; the next new dismissal removes the expired rows in the same call and is accepted.
        await BackdateDismissalsAsync(household, host.Time.Now.AddDays(-(7 * 12 + 1)));
        await DismissAsync(owner.Client, household, "en til");
        await using var after = TestDbContext.Create(db.ConnectionString, household);
        Assert.Equal(1, after.DismissedGrocerySuggestions.Count());
    }
}
