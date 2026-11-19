using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Import;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Imports;
using Foodplanner.Infrastructure.Import.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Tests.Recipes;

/// <summary>The AI fallback of the recipe import (T-077), always with a fake client.</summary>
public sealed class AiImportTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    // Line 0 is matched by the rules; line 1 has an unknown ingredient; line 2 cannot be read.
    private const string Recipe = """
        Test
        Porsjoner: 4

        Ingredienser
        2 dl melk
        2 dl dragefrukt
        ???

        Fremgangsmåte
        1. Rør.
        """;

    private const string Resolved = """
        Test
        Ingredienser
        2 dl melk
        Fremgangsmåte
        1. Rør.
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AuthHost Host(FakeRecipeLineAi? ai, params (string Key, string Value)[] settings)
    {
        var all = new Dictionary<string, string?>
        {
            ["AiImport:Enabled"] = "true",
            ["AiImport:ApiKey"] = "test-key-not-real",
            ["AiImport:GlobalMonthlyCalls"] = "1000000",
        };
        foreach (var (key, value) in settings)
        {
            all[key] = value;
        }

        var host = AuthHost.Create(
            db.ConnectionString,
            all,
            configureServices: services =>
            {
                if (ai is not null)
                {
                    services.RemoveAll<IRecipeLineAi>();
                    services.AddSingleton<IRecipeLineAi>(ai);
                }
            });
        host.DefaultLanguage = "nb";
        return host;
    }

    private static async Task<JsonElement> ImportAsync(ApiClient client, Guid household, string text = Recipe)
    {
        using var response = await client.PostAsync($"/api/households/{household}/imports", new { text });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.JsonAsync();
    }

    private static string Assist(JsonElement import) => import.GetProperty("aiAssist").GetString()!;

    /// <summary>One letter per line: T when the line is marked AI-suggested, F when not.</summary>
    private static string Marks(JsonElement import) =>
        string.Concat(import.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("aiSuggested").GetBoolean() ? "T" : "F"));

    private static async Task<(SignedInUser User, Guid Household)> NewHouseholdAsync(AuthHost host)
    {
        var user = await host.SignInAsync();
        return (user, await user.Client.CreateHouseholdAsync());
    }

    // ---- Off by default, nothing leaves the server -------------------------------------------------------------------------

    [Fact]
    public async Task The_default_options_are_off_and_a_test_host_never_reaches_the_real_client()
    {
        Assert.False(new AiImportOptions().Enabled);
        Assert.False(new AiImportOptions().IsActive);

        await using var host = AuthHost.Create(db.ConnectionString);
        host.DefaultLanguage = "nb";
        Assert.False(host.Services.GetRequiredService<IOptions<AiImportOptions>>().Value.IsActive);
        Assert.IsType<NoRealAiClient>(host.Services.GetRequiredService<IRecipeLineAi>());

        var (user, household) = await NewHouseholdAsync(host);
        var import = await ImportAsync(user.Client, household);
        Assert.Equal("NotUsed", Assist(import));
        Assert.DoesNotContain('T', Marks(import));
    }

    [Fact]
    public async Task Disabled_means_no_call_and_no_counting()
    {
        var ai = FakeRecipeLineAi.Helpful();
        await using var host = Host(ai, ("AiImport:Enabled", "false"));
        var (user, household) = await NewHouseholdAsync(host);

        var import = await ImportAsync(user.Client, household);

        Assert.Equal(0, ai.Calls);
        Assert.Equal("NotUsed", Assist(import));
        Assert.Equal(0, await CountRowsAsync(household));
    }

    [Fact]
    public async Task Enabled_without_a_key_behaves_as_disabled()
    {
        var ai = FakeRecipeLineAi.Helpful();
        await using var host = Host(ai, ("AiImport:ApiKey", ""));
        var (user, household) = await NewHouseholdAsync(host);

        var import = await ImportAsync(user.Client, household);

        Assert.Equal(0, ai.Calls);
        Assert.Equal("NotUsed", Assist(import));
    }

    [Fact]
    public async Task Nothing_unresolved_means_no_call()
    {
        var ai = FakeRecipeLineAi.Helpful();
        await using var host = Host(ai);
        var (user, household) = await NewHouseholdAsync(host);

        var import = await ImportAsync(user.Client, household, Resolved);

        Assert.Equal(0, ai.Calls);
        Assert.Equal("NotUsed", Assist(import));
        Assert.True(import.GetProperty("canSave").GetBoolean());
        Assert.Equal(0, await CountRowsAsync(household));
    }

    // ---- What is sent ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_batched_call_carries_only_the_unresolved_lines_and_the_unit_codes()
    {
        var ai = FakeRecipeLineAi.Helpful();
        await using var host = Host(ai);
        var (user, household) = await NewHouseholdAsync(host);

        await ImportAsync(user.Client, household);

        var request = Assert.Single(ai.Requests);
        Assert.Equal(["2 dl dragefrukt", "???"], request.Lines);
        Assert.Equal([.. UnitSeed.All.Select(u => u.Code).Order(StringComparer.Ordinal)], request.UnitCodes);
        // Nothing but strings: no ids, no household, user, recipe or email can be in the request type (see the Anthropic client test for the wire form).
        var json = JsonSerializer.Serialize(request);
        Assert.DoesNotContain(household.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(user.User.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(user.User.Email, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("melk", json, StringComparison.OrdinalIgnoreCase);
    }

    // ---- The result ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Valid_suggestions_fill_the_lines_marked_as_ai_suggested_and_the_marks_are_stored_and_cleared_by_an_edit()
    {
        var ai = FakeRecipeLineAi.Helpful();
        await using var host = Host(ai);
        var (user, household) = await NewHouseholdAsync(host);

        var import = await ImportAsync(user.Client, household);

        Assert.Equal("Applied", Assist(import));
        Assert.Equal("FTT", Marks(import));
        Assert.True(import.GetProperty("canSave").GetBoolean());
        var lines = import.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal("Smør", lines[1].GetProperty("ingredientName").GetString());
        Assert.Equal(3m, lines[1].GetProperty("quantity").GetDecimal());
        Assert.Equal("ToTaste", lines[2].GetProperty("amountKind").GetString());
        Assert.Equal("2 dl dragefrukt", lines[1].GetProperty("original").GetString()); // The import artifact is kept.

        // The marks are part of the stored draft: a later read still has them.
        var id = import.GetProperty("id").GetGuid();
        using var read = await user.Client.GetAsync($"/api/households/{household}/imports/{id}");
        Assert.Equal("FTT", Marks(await read.JsonAsync()));

        // Editing a line is the person's own choice: its mark goes away, the other stays.
        using var edit = await user.Client.PutAsync($"/api/households/{household}/imports/{id}", new
        {
            lines = new object[] { new { position = 1, ingredientId = IngredientSeed.All.Single(r => r.Key == "melk").Id, quantity = 1, unitId = UnitSeed.All.Single(u => u.Code == "dl").Id } },
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal("FFT", Marks(await edit.JsonAsync()));
    }

    [Fact]
    public async Task Unknown_ingredients_and_units_and_bad_amounts_are_dropped_and_the_rule_based_lines_stay()
    {
        var ai = new FakeRecipeLineAi(_ =>
        [
            new AiLineSuggestion(0, "ukjent-frukt-xyz", 2, null, "dl", "Measured", null, false, null, null),
            new AiLineSuggestion(1, "smør", 2, null, "kvarts", "Measured", null, false, null, null),
        ]);
        await using var host = Host(ai);
        var (user, household) = await NewHouseholdAsync(host);

        var import = await ImportAsync(user.Client, household);

        Assert.Equal(1, ai.Calls);
        Assert.Equal("Applied", Assist(import)); // The call worked; its lines did not pass.
        Assert.DoesNotContain('T', Marks(import));
        Assert.False(import.GetProperty("canSave").GetBoolean());
        var lines = import.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal("UnmatchedIngredient", lines[1].GetProperty("status").GetString());
        Assert.Equal("Unparsed", lines[2].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Another_households_own_ingredient_is_not_in_the_catalog_the_ai_may_use()
    {
        await using var host = Host(null);
        var (other, otherHousehold) = await NewHouseholdAsync(host);
        using (var created = await other.Client.PostAsync($"/api/households/{otherHousehold}/ingredients", new { name = "Hemmelig drage-saus", dimension = "Mass" }))
        {
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Ct));
        }

        var ai = new FakeRecipeLineAi(_ => [new AiLineSuggestion(0, "Hemmelig drage-saus", 1, null, "g", "Measured", null, false, null, null)]);
        await using var host2 = Host(ai);
        var (user, household) = await NewHouseholdAsync(host2);

        var import = await ImportAsync(user.Client, household);

        Assert.Equal(1, ai.Calls);
        Assert.DoesNotContain('T', Marks(import));
    }

    // ---- Failures keep the rule-based result -------------------------------------------------------------------------------

    public static TheoryData<string> Failures => new() { "error", "timeout", "cancelled", "invalid" };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Any_failure_keeps_the_rule_based_result_and_says_the_ai_was_unavailable(string failure)
    {
        var ai = new FakeRecipeLineAi(async (_, ct) =>
        {
            switch (failure)
            {
                case "timeout":
                    await Task.Delay(Timeout.Infinite, ct); // Cancelled by the assistant's own deadline.
                    break;
                case "cancelled":
                    throw new TaskCanceledException("http timeout");
                case "invalid":
                    throw new AiUnavailableException("The AI answer was not valid JSON.");
                default:
                    throw new AiUnavailableException("The AI service answered with status 500.");
            }

            return [];
        });
        await using var host = Host(ai, ("AiImport:TimeoutSeconds", "1"));
        var (user, household) = await NewHouseholdAsync(host);

        var import = await ImportAsync(user.Client, household);

        Assert.Equal(1, ai.Calls);
        Assert.Equal("Unavailable", Assist(import));
        Assert.DoesNotContain('T', Marks(import));
        Assert.False(import.GetProperty("canSave").GetBoolean());
        Assert.Equal("Matched", import.GetProperty("lines")[0].GetProperty("status").GetString()); // The rules' work is intact.
    }

    /// <summary>An answer whose enumeration blows up while the merger reads it.</summary>
    private sealed class ExplodingAnswer : IReadOnlyList<AiLineSuggestion?>
    {
        public int Count => 1;

        public AiLineSuggestion? this[int index] => throw new InvalidOperationException("boom");

        public IEnumerator<AiLineSuggestion?> GetEnumerator() => throw new InvalidOperationException("boom");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task A_failure_while_merging_the_answer_is_unavailable_and_the_rule_based_draft_stands()
    {
        var ai = new FakeRecipeLineAi((_, _) => Task.FromResult<IReadOnlyList<AiLineSuggestion?>>(new ExplodingAnswer()));
        await using var host = Host(ai);
        var (user, household) = await NewHouseholdAsync(host);

        var import = await ImportAsync(user.Client, household);

        Assert.Equal(1, ai.Calls);
        Assert.Equal("Unavailable", Assist(import));
        Assert.DoesNotContain('T', Marks(import));
        Assert.Equal("Matched", import.GetProperty("lines")[0].GetProperty("status").GetString());
    }

    // ---- Limits ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_daily_limit_stops_the_calls_with_a_note_and_the_next_day_starts_again()
    {
        var ai = FakeRecipeLineAi.Helpful();
        await using var host = Host(ai, ("AiImport:DailyCallsPerHousehold", "2"));
        var (user, household) = await NewHouseholdAsync(host);

        Assert.Equal("Applied", Assist(await ImportAsync(user.Client, household)));
        Assert.Equal("Applied", Assist(await ImportAsync(user.Client, household)));
        var third = await ImportAsync(user.Client, household);

        Assert.Equal("LimitReached", Assist(third));
        Assert.DoesNotContain('T', Marks(third));
        Assert.Equal(2, ai.Calls);

        host.Time.Now = host.Time.Now.AddDays(1);
        Assert.Equal("Applied", Assist(await ImportAsync(user.Client, household)));
        Assert.Equal(3, ai.Calls);
    }

    [Fact]
    public async Task The_monthly_limit_stops_the_calls_and_a_new_month_restarts_the_count()
    {
        var ai = FakeRecipeLineAi.Helpful();
        await using var host = Host(ai, ("AiImport:MonthlyCallsPerHousehold", "2"));
        var (user, household) = await NewHouseholdAsync(host);

        await ImportAsync(user.Client, household);
        await ImportAsync(user.Client, household);
        Assert.Equal("LimitReached", Assist(await ImportAsync(user.Client, household)));
        Assert.Equal(2, ai.Calls);

        // The stored month is last month: the counters restart at one.
        await HouseholdScenario.ScalarAsync(
            db.ConnectionString,
            "UPDATE AiHouseholdUsages SET MonthStart = DATEADD(month, -1, MonthStart), Day = DATEADD(day, -1, Day) WHERE HouseholdId = @h",
            ("@h", household));
        Assert.Equal("Applied", Assist(await ImportAsync(user.Client, household)));
        Assert.Equal((1L, 1L), await CountersAsync(household));
    }

    [Fact]
    public async Task The_global_monthly_cap_stops_every_household_and_gives_the_household_call_back()
    {
        var ai = FakeRecipeLineAi.Helpful();
        // The database is shared by the tests of this class, so the cap is relative to what is already counted this month.
        await using var probe = Host(null);
        var month = new DateOnly(probe.Time.Now.UtcDateTime.Year, probe.Time.Now.UtcDateTime.Month, 1);
        var used = Convert.ToInt32(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT ISNULL(SUM(Calls), 0) FROM AiGlobalUsages WHERE MonthStart = @m", ("@m", month)));

        await using var host = Host(ai, ("AiImport:GlobalMonthlyCalls", (used + 2).ToString()));
        var (a, householdA) = await NewHouseholdAsync(host);
        var (b, householdB) = await NewHouseholdAsync(host);

        Assert.Equal("Applied", Assist(await ImportAsync(a.Client, householdA)));
        Assert.Equal("Applied", Assist(await ImportAsync(b.Client, householdB)));
        Assert.Equal("LimitReached", Assist(await ImportAsync(a.Client, householdA)));
        Assert.Equal(2, ai.Calls);

        // The refused import did not cost household A a call.
        Assert.Equal((1L, 1L), await CountersAsync(householdA));
    }

    [Fact]
    public async Task One_household_using_its_limit_does_not_affect_another_household()
    {
        var ai = FakeRecipeLineAi.Helpful();
        await using var host = Host(ai, ("AiImport:DailyCallsPerHousehold", "1"));
        var (a, householdA) = await NewHouseholdAsync(host);
        var (b, householdB) = await NewHouseholdAsync(host);

        Assert.Equal("Applied", Assist(await ImportAsync(a.Client, householdA)));
        Assert.Equal("LimitReached", Assist(await ImportAsync(a.Client, householdA)));
        Assert.Equal("Applied", Assist(await ImportAsync(b.Client, householdB)));

        Assert.Equal((1L, 1L), await CountersAsync(householdA));
        Assert.Equal((1L, 1L), await CountersAsync(householdB));
    }

    [Fact]
    public async Task Two_imports_at_the_last_call_do_not_both_pass()
    {
        var ai = FakeRecipeLineAi.Helpful();
        await using var host = Host(ai, ("AiImport:DailyCallsPerHousehold", "3"));
        var (user, household) = await NewHouseholdAsync(host);
        await ImportAsync(user.Client, household);
        await ImportAsync(user.Client, household); // 2 of 3 used: one call left.

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => ImportAsync(user.Client, household)));

        Assert.Equal(1, results.Count(r => Assist(r) == "Applied"));
        Assert.Equal(1, results.Count(r => Assist(r) == "LimitReached"));
        Assert.Equal(3, ai.Calls);
        Assert.Equal((3L, 3L), await CountersAsync(household));
    }

    [Fact]
    public async Task Many_imports_at_once_from_a_new_household_never_pass_the_limit_even_when_the_counter_row_does_not_exist_yet()
    {
        var ai = FakeRecipeLineAi.Helpful();
        await using var host = Host(ai, ("AiImport:DailyCallsPerHousehold", "5"));
        var (user, household) = await NewHouseholdAsync(host);
        Assert.Equal(0, await CountRowsAsync(household));

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => ImportAsync(user.Client, household)));

        Assert.Equal(5, results.Count(r => Assist(r) == "Applied"));
        Assert.Equal(7, results.Count(r => Assist(r) == "LimitReached"));
        Assert.Equal(5, ai.Calls);
        Assert.Equal((5L, 5L), await CountersAsync(household));
        Assert.Equal(1, await CountRowsAsync(household));
    }

    [Fact]
    public async Task A_failed_call_still_counts_because_it_may_have_cost_money()
    {
        var ai = new FakeRecipeLineAi((_, _) => throw new AiUnavailableException("down"));
        await using var host = Host(ai);
        var (user, household) = await NewHouseholdAsync(host);

        await ImportAsync(user.Client, household);

        Assert.Equal((1L, 1L), await CountersAsync(household));
    }

    private async Task<long> CountRowsAsync(Guid household) =>
        Convert.ToInt64(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM AiHouseholdUsages WHERE HouseholdId = @h", ("@h", household)));

    private async Task<(long Day, long Month)> CountersAsync(Guid household)
    {
        var day = Convert.ToInt64(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT DayCalls FROM AiHouseholdUsages WHERE HouseholdId = @h", ("@h", household)));
        var month = Convert.ToInt64(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT MonthCalls FROM AiHouseholdUsages WHERE HouseholdId = @h", ("@h", household)));
        return (day, month);
    }
}
