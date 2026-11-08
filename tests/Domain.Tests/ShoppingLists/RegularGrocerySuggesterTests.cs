using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Tests.ShoppingLists;

public class RegularGrocerySuggesterTests
{
    // 2026-11-02 is a Monday; week 0 is the current week, week -1 the one before, and so on.
    private static readonly DateOnly Current = new(2026, 11, 2);
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private static DateOnly Week(int offset) => Current.AddDays(7 * offset);

    private static IEnumerable<ManualPurchase> Bought(string label, params int[] weekOffsets) =>
        weekOffsets.Select(o => new ManualPurchase(Week(o), label));

    private static IReadOnlyList<GrocerySuggestion> Suggest(IEnumerable<ManualPurchase> purchases, IReadOnlySet<string>? excluded = null, SuggestionRules? rules = null) =>
        RegularGrocerySuggester.Suggest(purchases, Current, excluded ?? None, rules);

    [Fact]
    public void A_label_bought_every_week_is_suggested_as_weekly()
    {
        var suggestion = Assert.Single(Suggest(Bought("Melk", -5, -4, -3, -2, -1, 0)));
        Assert.Equal(GroceryFrequency.Weekly, suggestion.Frequency);
        Assert.Equal(6, suggestion.WeeksSeen);
        Assert.Equal(12, suggestion.WeeksLooked);
        Assert.Equal("Melk", suggestion.Label);
        Assert.Equal(GroceryLabel.Normalize("Melk"), suggestion.Key);
    }

    [Fact]
    public void A_label_bought_every_second_week_is_suggested_as_every_second_week()
    {
        var suggestion = Assert.Single(Suggest(Bought("Brød", -10, -8, -6, -4, -2, 0)));
        Assert.Equal(GroceryFrequency.EveryTwoWeeks, suggestion.Frequency);
        Assert.Equal(6, suggestion.WeeksSeen);
    }

    [Fact]
    public void A_gap_of_three_weeks_is_monthly_and_reaches_four_weeks_in_the_window()
    {
        var suggestion = Assert.Single(Suggest(Bought("Vaskemiddel", -9, -6, -3, 0)));
        Assert.Equal(GroceryFrequency.Monthly, suggestion.Frequency);
        Assert.Equal(4, suggestion.WeeksSeen);
    }

    [Fact]
    public void The_defaults_are_three_for_monthly_and_four_for_weekly_and_every_second_week()
    {
        Assert.Equal(3, SuggestionRules.Default.MinWeeksMonthly);
        Assert.Equal(4, SuggestionRules.Default.MinWeeksWeekly);
        Assert.Equal(4, SuggestionRules.Default.MinWeeksEveryTwoWeeks);
    }

    [Theory]
    [InlineData(new[] { -8, -4, 0 })] // gaps 4, 4
    [InlineData(new[] { -11, -7, -3 })]
    [InlineData(new[] { -10, -6, -1 })] // gaps 4, 5
    [InlineData(new[] { -10, -5, 0 })] // gaps 5, 5
    public void Three_purchases_in_a_steady_monthly_rhythm_are_suggested_as_monthly(int[] offsets)
    {
        var suggestion = Assert.Single(Suggest(Bought("Kaffefilter", offsets)));

        Assert.Equal(GroceryFrequency.Monthly, suggestion.Frequency);
        Assert.Equal(3, suggestion.WeeksSeen);
    }

    [Theory]
    [InlineData(new[] { -2, -1, 0 })] // consecutive weeks
    [InlineData(new[] { -9, -8, -7 })]
    [InlineData(new[] { -9, -6, -3 })] // gaps 3, 3: monthly by median but not a steady monthly rhythm
    [InlineData(new[] { -11, -5, 0 })] // gaps 6, 5
    [InlineData(new[] { -11, -10, -6 })] // gaps 1, 4
    [InlineData(new[] { -10, -9, 0 })] // gaps 1, 9
    [InlineData(new[] { -4, 0 })] // two is too few
    public void Three_purchases_without_a_monthly_rhythm_give_no_suggestion(int[] offsets) =>
        Assert.Empty(Suggest(Bought("Kaffefilter", offsets)));

    [Theory]
    [InlineData(new[] { -12, -8, -4 })] // the oldest is just outside the window: two are left
    [InlineData(new[] { -12, -7, -2 })]
    [InlineData(new[] { -4, 0, 4 })] // a future week is ignored
    public void A_monthly_item_with_a_sighting_outside_the_window_is_not_suggested(int[] offsets) =>
        Assert.Empty(Suggest(Bought("Kaffefilter", offsets)));

    [Fact]
    public void A_monthly_item_with_sightings_at_the_window_edges_is_suggested()
    {
        var suggestion = Assert.Single(Suggest(Bought("Kaffefilter", -11, -6, -1)));

        Assert.Equal(GroceryFrequency.Monthly, suggestion.Frequency);
        Assert.Equal(3, suggestion.WeeksSeen);
        Assert.Single(Suggest(Bought("Kaffefilter", -11, -7, -3)));
    }

    [Fact]
    public void Two_sightings_in_one_week_count_once_so_they_are_not_a_third_week()
    {
        var purchases = Bought("Kaffefilter", -8, -4).Append(new ManualPurchase(Week(-4), "kaffefilter"));
        Assert.Empty(Suggest(purchases));
    }

    [Fact]
    public void Monthly_with_four_weeks_seen_still_uses_the_median_rule()
    {
        var suggestion = Assert.Single(Suggest(Bought("Vaskemiddel", -9, -6, -3, 0)));

        Assert.Equal(GroceryFrequency.Monthly, suggestion.Frequency);
        Assert.Equal(4, suggestion.WeeksSeen);
    }

    [Theory]
    [InlineData(new[] { -2, -1, 0 })]
    [InlineData(new[] { -4, -2, 0 })] // every second week with three
    public void Weekly_and_every_second_week_still_need_four_weeks(int[] offsets) =>
        Assert.Empty(Suggest(Bought("Melk", offsets)));

    [Theory]
    [InlineData(new[] { -3, -2, -1, 0 }, true)]
    [InlineData(new[] { -2, -1, 0 }, false)]
    [InlineData(new[] { 0 }, false)]
    public void Four_weeks_are_needed_and_three_or_fewer_give_nothing(int[] offsets, bool suggested) =>
        Assert.Equal(suggested, Suggest(Bought("Melk", offsets)).Count == 1);

    [Fact]
    public void Several_items_with_the_same_label_in_one_week_count_as_one_week()
    {
        var purchases = Bought("Melk", -2, -2, -1, -1, 0, 0);

        Assert.Empty(Suggest(purchases)); // 3 weeks, not 6 items
        Assert.Single(Suggest(purchases.Append(new ManualPurchase(Week(-3), "melk"))));
    }

    [Fact]
    public void Weeks_outside_the_window_do_not_count_and_the_window_ends_with_the_current_week()
    {
        Assert.Empty(Suggest(Bought("Melk", -12, -13, -14, 0))); // 12 weeks ago is just outside
        var inWindow = Assert.Single(Suggest(Bought("Melk", -11, -10, -9, -8)));
        Assert.Equal(4, inWindow.WeeksSeen);
        Assert.Empty(Suggest(Bought("Melk", -2, -1, 1, 2))); // future weeks are not looked at
    }

    [Fact]
    public void Labels_that_differ_in_case_whitespace_diacritics_or_plural_are_one_label_and_the_latest_spelling_wins()
    {
        var purchases = new[]
        {
            new ManualPurchase(Week(-3), "oster"),
            new ManualPurchase(Week(-2), "  OST "),
            new ManualPurchase(Week(-1), "Ost"),
            new ManualPurchase(Week(0), " Oster"),
        };

        var suggestion = Assert.Single(Suggest(purchases));

        Assert.Equal("Oster", suggestion.Label); // trimmed, the most recent spelling
        Assert.Equal(4, suggestion.WeeksSeen);
    }

    [Fact]
    public void A_label_in_the_same_latest_week_in_two_spellings_is_decided_by_ordinal_order_not_input_order()
    {
        var a = new[] { new ManualPurchase(Week(0), "melk"), new ManualPurchase(Week(0), "Melk") };
        var weeks = Bought("Melk", -3, -2, -1);

        var forward = Suggest(weeks.Concat(a));
        var backward = Suggest(weeks.Concat(a.Reverse()));

        Assert.Equal(forward[0].Label, backward[0].Label);
        Assert.Equal("Melk", forward[0].Label);
    }

    [Fact]
    public void Different_things_are_not_merged()
    {
        var purchases = Bought("Melk", -3, -2).Concat(Bought("Melon", -1, 0));

        Assert.Empty(Suggest(purchases)); // 2 + 2, not 4
    }

    [Fact]
    public void A_label_matching_an_excluded_key_is_skipped_through_normalisation()
    {
        var excluded = new HashSet<string> { GroceryLabel.Normalize("Melk") };

        Assert.Empty(Suggest(Bought("MELK ", -3, -2, -1, 0), excluded));
        Assert.Single(Suggest(Bought("Melon", -3, -2, -1, 0), excluded));
    }

    [Fact]
    public void A_label_seen_often_enough_but_with_a_median_gap_beyond_a_month_is_not_suggested()
    {
        // The median, not the longest gap, decides: gaps 1, 9, 1 have median 1 (weekly by rhythm).
        Assert.Equal(GroceryFrequency.Weekly, Assert.Single(Suggest(Bought("Egg", -11, -10, -1, 0))).Frequency);

        // Gaps of 8 weeks exceed anything monthly (a longer window is needed to see four of them).
        var rules = SuggestionRules.Default with { WindowWeeks = 40 };
        var far = RegularGrocerySuggester.Suggest(Bought("Egg", -32, -24, -16, -8, 0), Current, None, rules);
        Assert.Empty(far);
    }

    [Theory]
    [InlineData(new[] { 0, 1, 2, 3 }, GroceryFrequency.Weekly)] // gaps 1,1,1
    [InlineData(new[] { 0, 1, 2, 4 }, GroceryFrequency.Weekly)] // 1,1,2 -> median 1
    [InlineData(new[] { 0, 2, 4, 5 }, GroceryFrequency.EveryTwoWeeks)] // 2,2,1 -> median 2
    [InlineData(new[] { 0, 1, 3, 5 }, GroceryFrequency.EveryTwoWeeks)] // 1,2,2 -> median 2
    [InlineData(new[] { 0, 1, 3, 4, 6 }, GroceryFrequency.EveryTwoWeeks)] // 1,2,1,2 -> median 1.5
    [InlineData(new[] { 0, 3, 6, 9 }, GroceryFrequency.Monthly)] // 3,3,3
    [InlineData(new[] { 0, 4, 8, 13 }, GroceryFrequency.Monthly)] // 4,4,5
    [InlineData(new[] { 0, 6, 12 }, GroceryFrequency.Monthly)] // 6,6
    public void The_median_gap_decides_the_frequency(int[] weeks, GroceryFrequency expected) =>
        Assert.Equal(expected, RegularGrocerySuggester.Classify([.. weeks.Select(w => Current.AddDays(7 * w))]));

    [Theory]
    [InlineData(new[] { 0, 7, 14 })] // gaps of 7: not a rhythm of a regular grocery
    [InlineData(new[] { 0 })]
    public void A_gap_longer_than_a_month_or_a_single_week_has_no_rhythm(int[] weeks) =>
        Assert.Null(RegularGrocerySuggester.Classify([.. weeks.Select(w => Current.AddDays(7 * w))]));

    [Fact]
    public void Suggestions_are_ordered_by_weeks_seen_then_key_and_do_not_depend_on_input_order()
    {
        var purchases = Bought("Smør", -4, -3, -2, -1).Concat(Bought("Melk", -6, -5, -4, -3, -2, -1)).Concat(Bought("Brød", -4, -3, -2, -1)).ToList();

        var forward = Suggest(purchases);
        var backward = Suggest(Enumerable.Reverse(purchases));

        Assert.Equal(["melk", "brød", "smør"], forward.Select(s => s.Key));
        Assert.Equal(forward, backward);
    }

    [Fact]
    public void Empty_or_punctuation_only_labels_are_ignored()
    {
        Assert.Empty(Suggest(Bought("  ", -3, -2, -1, 0).Concat(Bought("-", -3, -2, -1, 0))));
    }

    [Fact]
    public void The_window_is_twelve_weeks_ending_with_the_current_week()
    {
        Assert.Equal(Week(-11), RegularGrocerySuggester.WindowStart(Current));
        Assert.Equal(Week(-3), RegularGrocerySuggester.WindowStart(Current, SuggestionRules.Default with { WindowWeeks = 4 }));
    }

    [Fact]
    public void A_dismissal_hides_for_twelve_weeks_after_it_was_made_and_can_be_renewed()
    {
        var at = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var dismissed = new DismissedGrocerySuggestion(GroceryLabel.Normalize("Melk"), at);

        Assert.Equal(at.AddDays(84), dismissed.HiddenUntil());
        dismissed.Renew(at.AddDays(90));
        Assert.Equal(at.AddDays(174), dismissed.HiddenUntil());
        Assert.Throws<ArgumentException>(() => new DismissedGrocerySuggestion(string.Empty, at));
        Assert.Throws<ArgumentException>(() => new DismissedGrocerySuggestion(new string('x', 101), at));
    }
}
