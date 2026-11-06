using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Tests.ShoppingLists;

/// <summary>Verifier probes (T-110): realistic and irregular patterns, the window edges, and the one-line monthly change.</summary>
public class RegularGrocerySuggesterRhythmTests
{
    private static readonly DateOnly Current = new(2026, 11, 2);
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private static DateOnly Week(int offset) => Current.AddDays(7 * offset);

    private static GroceryFrequency? Suggested(params int[] offsets) =>
        RegularGrocerySuggester.Suggest(offsets.Select(o => new ManualPurchase(Week(o), "Kaffe")), Current, None).SingleOrDefault()?.Frequency;

    [Fact]
    public void Weekly_with_a_few_holes_is_still_weekly() => Assert.Equal(GroceryFrequency.Weekly, Suggested(-11, -10, -8, -7, -6, -4, -3, -1, 0));

    [Fact]
    public void Every_second_week_with_one_skipped_is_still_every_second_week() =>
        Assert.Equal(GroceryFrequency.EveryTwoWeeks, Suggested(-10, -8, -6, -2, 0));

    [Fact]
    public void A_burst_then_nothing_has_median_one_and_is_weekly_even_if_old() =>
        Assert.Equal(GroceryFrequency.Weekly, Suggested(-11, -10, -9, -8)); // known: no recency test (documented in domain.md)

    [Fact]
    public void Two_bursts_far_apart_are_called_weekly_because_the_median_gap_is_one() =>
        Assert.Equal(GroceryFrequency.Weekly, Suggested(-11, -10, -1, 0)); // known limit of the median rule: gaps 1, 9, 1

    [Fact]
    public void Irregular_gaps_one_two_three_five_have_median_two_and_a_half_so_every_second_week() =>
        Assert.Equal(GroceryFrequency.EveryTwoWeeks, Suggested(-11, -10, -7, -2, 0)); // gaps 1, 3, 5, 2 -> sorted 1,2,3,5 -> 2.5

    [Fact]
    public void A_gap_of_five_or_six_weeks_between_two_weeks_is_monthly_by_classify_but_never_reaches_four_weeks_seen() =>
        Assert.Equal(GroceryFrequency.Monthly, RegularGrocerySuggester.Classify([Week(-11), Week(-5), Week(0)])); // gaps 6,5 -> 5.5

    [Fact]
    public void Four_weeks_in_the_window_edges_count_and_the_week_before_the_window_or_after_the_current_one_does_not()
    {
        Assert.Equal(GroceryFrequency.Weekly, Suggested(-11, -10, -9, -8));
        Assert.Null(Suggested(-12, -11, -10, -9)); // -12 is outside: only three weeks
        Assert.Null(Suggested(-3, -2, -1, 1)); // next week is not in the window
        Assert.Equal(GroceryFrequency.Weekly, Suggested(-3, -2, -1, 0));
    }

    [Fact]
    public void A_label_twice_in_one_week_counts_once()
    {
        var purchases = Enumerable.Range(-2, 3).SelectMany(o => new[] { new ManualPurchase(Week(o), "Kaffe"), new ManualPurchase(Week(o), "kaffe") });
        Assert.Empty(RegularGrocerySuggester.Suggest(purchases, Current, None));
    }

    [Fact]
    public void Monthly_becomes_suggestible_by_changing_one_constant_only()
    {
        var monthlyOffsets = new[] { -9, -5, -1 }; // gap 4
        var purchases = monthlyOffsets.Select(o => new ManualPurchase(Week(o), "Toalettpapir")).ToList();
        Assert.Empty(RegularGrocerySuggester.Suggest(purchases, Current, None));
        var lowered = RegularGrocerySuggester.Suggest(purchases, Current, None, new SuggestionRules { MinWeeksMonthly = 3 });
        Assert.Equal(GroceryFrequency.Monthly, Assert.Single(lowered).Frequency);
    }
}
