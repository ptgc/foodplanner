namespace Foodplanner.Domain.ShoppingLists;

/// <summary>An item somebody typed in by hand on the list of the week that starts on <see cref="WeekStart"/> (a Monday).</summary>
public sealed record ManualPurchase(DateOnly WeekStart, string Label);

/// <summary>
/// A label that looks like a regular grocery: <see cref="Label"/> is its most recent spelling, <see cref="Key"/> the normalised
/// comparison key, seen in <see cref="WeeksSeen"/> of the <see cref="WeeksLooked"/> weeks looked at.
/// </summary>
public sealed record GrocerySuggestion(string Key, string Label, GroceryFrequency Frequency, int WeeksSeen, int WeeksLooked);

/// <summary>
/// The tunable numbers of the learning (T-110, Q-030 point 4), all in one place so a rule can change without a migration.
/// Decided: look at the last 12 weeks, suggest weekly and every-second-week at 4 or more weeks, hide a dismissal for 12 weeks.
/// Monthly (T-111): a once-a-month item appears 3 times at most in 12 weeks, so 3 weeks are enough when the gaps are 4 to 5 weeks.
/// </summary>
public sealed record SuggestionRules
{
    public static SuggestionRules Default { get; } = new();

    /// <summary>The window: this many weeks, ending with the current week.</summary>
    public int WindowWeeks { get; init; } = 12;

    /// <summary>A dismissed suggestion stays hidden this many weeks.</summary>
    public int HiddenWeeks { get; init; } = 12;

    public int MinWeeksWeekly { get; init; } = 4;

    public int MinWeeksEveryTwoWeeks { get; init; } = 4;

    /// <summary>
    /// Monthly needs only 3 weeks (architect, T-111): a true monthly item is seen 3 times at most in 12 weeks. With fewer than
    /// <see cref="MinWeeksMonthlyLoose"/> weeks seen, every gap must be a steady monthly one (<see cref="MonthlyGapMin"/> to
    /// <see cref="MonthlyGapMax"/> weeks), so three weeks in a row or three odd weeks are not a monthly item.
    /// </summary>
    public int MinWeeksMonthly { get; init; } = 3;

    /// <summary>From this many weeks seen, the plain median rule decides Monthly (the gaps need not all be steady).</summary>
    public int MinWeeksMonthlyLoose { get; init; } = 4;

    public int MonthlyGapMin { get; init; } = 4;

    public int MonthlyGapMax { get; init; } = 5;

    /// <summary>A median gap below this is Weekly (gaps are whole weeks; the median of an even count can be x.5).</summary>
    public decimal WeeklyBelow { get; init; } = 1.5m;

    /// <summary>A median gap below this (and at least <see cref="WeeklyBelow"/>) is every second week.</summary>
    public decimal EveryTwoWeeksBelow { get; init; } = 3m;

    /// <summary>A median gap up to this (and at least <see cref="EveryTwoWeeksBelow"/>) is monthly; a longer one is no rhythm.</summary>
    public decimal MonthlyUpTo { get; init; } = 6m;

    public int MinWeeksFor(GroceryFrequency frequency) => frequency switch
    {
        GroceryFrequency.Weekly => MinWeeksWeekly,
        GroceryFrequency.EveryTwoWeeks => MinWeeksEveryTwoWeeks,
        _ => MinWeeksMonthly,
    };
}

/// <summary>
/// Pure analyser (T-110): which hand-added labels are bought regularly? The caller passes ONLY items added by hand (labels, no
/// ingredient): generated items and items with a weekly part come from recipes or are regular already, so they are not signal.
/// A label counts once per week. Rhythm: the median gap between the weeks it was seen in decides the frequency (about 1 = weekly,
/// about 2 = every second week, about 4 = monthly, see <see cref="SuggestionRules"/>); it must be seen in enough weeks for that
/// frequency. Labels in <c>excludedKeys</c> (already a regular grocery, or dismissed) are skipped.
/// </summary>
public static class RegularGrocerySuggester
{
    /// <summary>The Monday of the first week of the window that ends with <paramref name="currentWeek"/>.</summary>
    public static DateOnly WindowStart(DateOnly currentWeek, SuggestionRules? rules = null) =>
        currentWeek.AddDays(-7 * ((rules ?? SuggestionRules.Default).WindowWeeks - 1));

    public static IReadOnlyList<GrocerySuggestion> Suggest(
        IEnumerable<ManualPurchase> purchases, DateOnly currentWeek, IReadOnlySet<string> excludedKeys, SuggestionRules? rules = null)
    {
        rules ??= SuggestionRules.Default;
        var first = WindowStart(currentWeek, rules);
        var result = new List<GrocerySuggestion>();

        var byKey = purchases
            .Where(p => p.WeekStart >= first && p.WeekStart <= currentWeek)
            .Select(p => (Key: GroceryLabel.Normalize(p.Label), Purchase: p))
            .Where(p => p.Key.Length > 0 && !excludedKeys.Contains(p.Key))
            .GroupBy(p => p.Key, StringComparer.Ordinal);

        foreach (var group in byKey)
        {
            var weeks = group.Select(p => p.Purchase.WeekStart).Distinct().Order().ToList();
            if (Classify(weeks, rules) is not { } frequency || weeks.Count < rules.MinWeeksFor(frequency)
                || (frequency == GroceryFrequency.Monthly && weeks.Count < rules.MinWeeksMonthlyLoose && !SteadyMonthly(weeks, rules)))
            {
                continue;
            }

            // The most recent spelling; within one week the smallest ordinal, so the answer does not depend on input order.
            var label = group.OrderByDescending(p => p.Purchase.WeekStart).ThenBy(p => p.Purchase.Label, StringComparer.Ordinal)
                .First().Purchase.Label.Trim();
            result.Add(new GrocerySuggestion(group.Key, label, frequency, weeks.Count, rules.WindowWeeks));
        }

        return [.. result.OrderByDescending(s => s.WeeksSeen).ThenBy(s => s.Key, StringComparer.Ordinal)];
    }

    private static bool SteadyMonthly(IReadOnlyList<DateOnly> weeks, SuggestionRules rules)
    {
        for (var i = 1; i < weeks.Count; i++)
        {
            var gap = (weeks[i].DayNumber - weeks[i - 1].DayNumber) / 7;
            if (gap < rules.MonthlyGapMin || gap > rules.MonthlyGapMax)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The frequency of a rhythm from the median gap (in weeks) between the sorted distinct weeks; null = no rhythm.</summary>
    public static GroceryFrequency? Classify(IReadOnlyList<DateOnly> sortedDistinctWeeks, SuggestionRules? rules = null)
    {
        rules ??= SuggestionRules.Default;
        if (sortedDistinctWeeks.Count < 2)
        {
            return null;
        }

        var gaps = new List<int>();
        for (var i = 1; i < sortedDistinctWeeks.Count; i++)
        {
            gaps.Add((sortedDistinctWeeks[i].DayNumber - sortedDistinctWeeks[i - 1].DayNumber) / 7);
        }

        gaps.Sort();
        var middle = gaps.Count / 2;
        var median = gaps.Count % 2 == 1 ? gaps[middle] : (gaps[middle - 1] + gaps[middle]) / 2m;

        if (median < rules.WeeklyBelow)
        {
            return GroceryFrequency.Weekly;
        }

        if (median < rules.EveryTwoWeeksBelow)
        {
            return GroceryFrequency.EveryTwoWeeks;
        }

        return median <= rules.MonthlyUpTo ? GroceryFrequency.Monthly : null;
    }
}
