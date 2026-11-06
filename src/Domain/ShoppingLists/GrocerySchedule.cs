namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// Pure due-week rules for regular groceries (T-109, Q-030). A week is its ISO-8601 Monday.
///  - <see cref="GroceryFrequency.Weekly"/>: every week; the anchor is not used.
///  - <see cref="GroceryFrequency.EveryTwoWeeks"/>: the anchor week and every second week after it. A week BEFORE the anchor is
///    never due (the grocery did not exist yet, or the user moved it forward on purpose), so "next due" is never in the past.
///    Counted in whole weeks, so it runs straight over year ends and 53-week years (it never looks at ISO week numbers).
///  - <see cref="GroceryFrequency.Monthly"/>: the week whose Monday is the first Monday of a month, i.e. a Monday on day 1 to 7.
///    Every month has exactly one such week. A week that starts on 29 Dec and holds 1 Jan is December's last week, not
///    January's due week: January's is the week of its first Monday. The anchor is not used.
/// </summary>
public static class GrocerySchedule
{
    public static bool IsDue(GroceryFrequency frequency, DateOnly? startWeek, DateOnly weekStart)
    {
        RequireMonday(weekStart, nameof(weekStart));
        switch (frequency)
        {
            case GroceryFrequency.Weekly:
                return true;
            case GroceryFrequency.EveryTwoWeeks:
                var anchor = RequireAnchor(startWeek);
                var weeks = (weekStart.DayNumber - anchor.DayNumber) / 7;
                return weekStart >= anchor && weeks % 2 == 0;
            case GroceryFrequency.Monthly:
                return weekStart.Day <= 7;
            default:
                throw new ArgumentOutOfRangeException(nameof(frequency));
        }
    }

    /// <summary>The first due week that is <paramref name="fromWeek"/> or later.</summary>
    public static DateOnly NextDue(GroceryFrequency frequency, DateOnly? startWeek, DateOnly fromWeek)
    {
        RequireMonday(fromWeek, nameof(fromWeek));
        switch (frequency)
        {
            case GroceryFrequency.Weekly:
                return fromWeek;
            case GroceryFrequency.EveryTwoWeeks:
                var anchor = RequireAnchor(startWeek);
                if (fromWeek <= anchor)
                {
                    return anchor;
                }

                return IsDue(frequency, anchor, fromWeek) ? fromWeek : fromWeek.AddDays(7);
            case GroceryFrequency.Monthly:
                var week = fromWeek;
                while (week.Day > 7)
                {
                    week = week.AddDays(7);
                }

                return week;
            default:
                throw new ArgumentOutOfRangeException(nameof(frequency));
        }
    }

    /// <summary>The Monday of the ISO week that holds <paramref name="date"/>.</summary>
    public static DateOnly WeekStartOf(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static DateOnly RequireAnchor(DateOnly? startWeek)
    {
        if (startWeek is not { } anchor)
        {
            throw new ArgumentException("Every second week needs an anchor week.", nameof(startWeek));
        }

        RequireMonday(anchor, nameof(startWeek));
        return anchor;
    }

    private static void RequireMonday(DateOnly date, string name)
    {
        if (date.DayOfWeek != DayOfWeek.Monday)
        {
            throw new ArgumentException("A week is identified by its Monday.", name);
        }
    }
}
