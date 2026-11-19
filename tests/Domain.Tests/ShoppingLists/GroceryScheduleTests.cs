using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Tests.ShoppingLists;

public class GroceryScheduleTests
{
    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd");

    // ---- Weekly ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-01-05")]
    [InlineData("2026-12-28")]
    [InlineData("2027-01-04")]
    public void Weekly_is_due_every_week_and_next_due_is_the_week_itself(string week)
    {
        Assert.True(GrocerySchedule.IsDue(GroceryFrequency.Weekly, null, D(week)));
        Assert.Equal(D(week), GrocerySchedule.NextDue(GroceryFrequency.Weekly, null, D(week)));
    }

    // ---- Every second week -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-11-02", true)] // the anchor
    [InlineData("2026-11-09", false)]
    [InlineData("2026-11-16", true)]
    [InlineData("2026-11-23", false)]
    [InlineData("2026-11-30", true)]
    public void Every_second_week_is_due_on_the_anchor_and_every_second_week_after_it(string week, bool due)
    {
        Assert.Equal(due, GrocerySchedule.IsDue(GroceryFrequency.EveryTwoWeeks, D("2026-11-02"), D(week)));
    }

    [Theory]
    [InlineData("2026-10-26")] // one week before
    [InlineData("2026-10-19")] // two weeks before: would be on the 2-week rhythm, still not due
    [InlineData("2025-01-06")]
    public void Weeks_before_the_anchor_are_never_due(string week)
    {
        Assert.False(GrocerySchedule.IsDue(GroceryFrequency.EveryTwoWeeks, D("2026-11-02"), D(week)));
    }

    [Fact]
    public void Every_second_week_runs_straight_over_a_year_end()
    {
        var anchor = D("2026-12-07");
        Assert.True(GrocerySchedule.IsDue(GroceryFrequency.EveryTwoWeeks, anchor, D("2026-12-21")));
        Assert.False(GrocerySchedule.IsDue(GroceryFrequency.EveryTwoWeeks, anchor, D("2026-12-28")));
        Assert.True(GrocerySchedule.IsDue(GroceryFrequency.EveryTwoWeeks, anchor, D("2027-01-04")));
        Assert.False(GrocerySchedule.IsDue(GroceryFrequency.EveryTwoWeeks, anchor, D("2027-01-11")));
        Assert.True(GrocerySchedule.IsDue(GroceryFrequency.EveryTwoWeeks, anchor, D("2027-01-18")));
    }

    [Fact]
    public void Every_second_week_ignores_iso_week_numbers_across_a_53_week_year()
    {
        // 2026 has 53 ISO weeks (week 53 starts 2026-12-28). From an odd week the rhythm would flip if week numbers were used.
        var anchor = D("2026-12-14"); // ISO week 51
        var due = new List<DateOnly>();
        for (var week = D("2026-12-14"); week <= D("2027-02-01"); week = week.AddDays(7))
        {
            if (GrocerySchedule.IsDue(GroceryFrequency.EveryTwoWeeks, anchor, week))
            {
                due.Add(week);
            }
        }

        Assert.Equal(new[] { "2026-12-14", "2026-12-28", "2027-01-11", "2027-01-25" }.Select(D), due);
    }

    [Fact]
    public void Every_second_week_stays_two_weeks_apart_for_years()
    {
        var anchor = D("2025-03-03");
        DateOnly? previous = null;
        for (var week = anchor; week < D("2031-01-01"); week = week.AddDays(7))
        {
            if (!GrocerySchedule.IsDue(GroceryFrequency.EveryTwoWeeks, anchor, week))
            {
                continue;
            }

            if (previous is { } p)
            {
                Assert.Equal(14, week.DayNumber - p.DayNumber);
            }

            previous = week;
        }
    }

    [Theory]
    [InlineData("2026-11-02", "2026-10-26", "2026-11-02")] // before the anchor: the anchor
    [InlineData("2026-11-02", "2026-11-02", "2026-11-02")] // on the anchor
    [InlineData("2026-11-02", "2026-11-09", "2026-11-16")] // off-week: the next one
    [InlineData("2026-11-02", "2026-11-16", "2026-11-16")]
    [InlineData("2026-12-14", "2026-12-21", "2026-12-28")] // into week 53
    [InlineData("2026-12-14", "2026-12-28", "2026-12-28")] // week 53 itself
    [InlineData("2026-12-14", "2027-01-04", "2027-01-11")] // over the year end
    public void Next_due_for_every_second_week_is_the_first_due_week_from_the_given_week(string anchor, string from, string expected)
    {
        var next = GrocerySchedule.NextDue(GroceryFrequency.EveryTwoWeeks, D(anchor), D(from));

        Assert.Equal(D(expected), next);
        Assert.True(GrocerySchedule.IsDue(GroceryFrequency.EveryTwoWeeks, D(anchor), next));
    }

    [Fact]
    public void Every_second_week_without_an_anchor_is_refused()
    {
        Assert.Throws<ArgumentException>(() => GrocerySchedule.IsDue(GroceryFrequency.EveryTwoWeeks, null, D("2026-11-02")));
        Assert.Throws<ArgumentException>(() => GrocerySchedule.NextDue(GroceryFrequency.EveryTwoWeeks, null, D("2026-11-02")));
    }

    // ---- Monthly ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Monthly_is_due_in_the_week_of_the_first_monday_of_a_month()
    {
        Assert.True(GrocerySchedule.IsDue(GroceryFrequency.Monthly, null, D("2026-11-02")));
        Assert.False(GrocerySchedule.IsDue(GroceryFrequency.Monthly, null, D("2026-11-09")));
        Assert.False(GrocerySchedule.IsDue(GroceryFrequency.Monthly, null, D("2026-11-30"))); // 30 Nov holds 1 Dec, but December's is 7 Dec
        Assert.True(GrocerySchedule.IsDue(GroceryFrequency.Monthly, null, D("2026-12-07")));
    }

    [Fact]
    public void Monthly_with_a_monday_on_the_first_is_due_that_week_and_not_the_week_before()
    {
        Assert.True(GrocerySchedule.IsDue(GroceryFrequency.Monthly, null, D("2026-06-01")));
        Assert.False(GrocerySchedule.IsDue(GroceryFrequency.Monthly, null, D("2026-05-25")));
        Assert.False(GrocerySchedule.IsDue(GroceryFrequency.Monthly, null, D("2026-06-08")));
    }

    [Fact]
    public void Monthly_with_a_month_starting_mid_week_waits_for_the_first_monday()
    {
        // 1 July 2026 is a Wednesday: the week of Monday 29 June is June's last week, July's due week starts on 6 July.
        Assert.False(GrocerySchedule.IsDue(GroceryFrequency.Monthly, null, D("2026-06-29")));
        Assert.True(GrocerySchedule.IsDue(GroceryFrequency.Monthly, null, D("2026-07-06")));
    }

    [Fact]
    public void Monthly_across_a_year_end_and_a_53_week_year()
    {
        Assert.False(GrocerySchedule.IsDue(GroceryFrequency.Monthly, null, D("2026-12-28"))); // ISO week 53
        Assert.True(GrocerySchedule.IsDue(GroceryFrequency.Monthly, null, D("2027-01-04")));
        Assert.Equal(D("2027-01-04"), GrocerySchedule.NextDue(GroceryFrequency.Monthly, null, D("2026-12-28")));
    }

    [Fact]
    public void Every_month_for_many_years_has_exactly_one_monthly_due_week()
    {
        var perMonth = new Dictionary<(int Year, int Month), int>();
        for (var week = D("2020-01-06"); week < D("2036-01-01"); week = week.AddDays(7))
        {
            if (GrocerySchedule.IsDue(GroceryFrequency.Monthly, null, week))
            {
                perMonth[(week.Year, week.Month)] = perMonth.GetValueOrDefault((week.Year, week.Month)) + 1;
            }
        }

        // 2020-01-06 is the first Monday of January 2020; every month from then to December 2035 is covered once.
        Assert.Equal(16 * 12, perMonth.Count);
        Assert.All(perMonth, kv => Assert.Equal(1, kv.Value));
    }

    [Theory]
    [InlineData("2026-11-02", "2026-11-02")]
    [InlineData("2026-11-09", "2026-12-07")]
    [InlineData("2026-11-30", "2026-12-07")]
    [InlineData("2026-12-14", "2027-01-04")]
    [InlineData("2027-05-03", "2027-05-03")]
    public void Next_due_for_monthly_is_the_first_due_week_from_the_given_week(string from, string expected)
    {
        var next = GrocerySchedule.NextDue(GroceryFrequency.Monthly, null, D(from));

        Assert.Equal(D(expected), next);
    }

    [Fact]
    public void Next_due_is_never_before_the_given_week_and_always_due()
    {
        foreach (var frequency in Enum.GetValues<GroceryFrequency>())
        {
            var anchor = frequency == GroceryFrequency.EveryTwoWeeks ? D("2026-03-02") : (DateOnly?)null;
            for (var from = D("2025-12-29"); from < D("2028-01-01"); from = from.AddDays(7))
            {
                var next = GrocerySchedule.NextDue(frequency, anchor, from);
                Assert.True(next >= from);
                Assert.True(GrocerySchedule.IsDue(frequency, anchor, next));
                Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
            }
        }
    }

    // ---- Input -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_week_must_be_a_monday()
    {
        Assert.Throws<ArgumentException>(() => GrocerySchedule.IsDue(GroceryFrequency.Weekly, null, D("2026-11-03")));
        Assert.Throws<ArgumentException>(() => GrocerySchedule.NextDue(GroceryFrequency.Monthly, null, D("2026-11-04")));
        Assert.Throws<ArgumentException>(() => GrocerySchedule.IsDue(GroceryFrequency.EveryTwoWeeks, D("2026-11-03"), D("2026-11-09")));
    }

    [Theory]
    [InlineData("2026-11-02", "2026-11-02")]
    [InlineData("2026-11-04", "2026-11-02")]
    [InlineData("2026-11-08", "2026-11-02")] // Sunday belongs to the week before the next Monday
    [InlineData("2026-11-09", "2026-11-09")]
    [InlineData("2027-01-01", "2026-12-28")] // Friday in ISO week 53 of 2026
    public void Week_start_of_is_the_iso_monday(string date, string expected)
    {
        Assert.Equal(D(expected), GrocerySchedule.WeekStartOf(D(date)));
    }

    // ---- The entity keeps frequency and anchor consistent ----------------------------------------------------------

    [Fact]
    public void A_new_grocery_is_weekly_without_an_anchor_and_is_due_every_week()
    {
        var grocery = new WeeklyGrocery(Guid.NewGuid(), null, null);

        Assert.Equal(GroceryFrequency.Weekly, grocery.Frequency);
        Assert.Null(grocery.StartWeek);
        Assert.True(grocery.IsDueIn(D("2026-11-09")));
        Assert.Equal(D("2026-11-09"), grocery.NextDueFrom(D("2026-11-09")));
    }

    [Fact]
    public void Only_every_second_week_has_an_anchor_and_it_needs_one_that_is_a_monday()
    {
        var grocery = new WeeklyGrocery(Guid.NewGuid(), null, null, GroceryFrequency.EveryTwoWeeks, D("2026-11-09"));
        Assert.Equal(D("2026-11-09"), grocery.StartWeek);
        Assert.False(grocery.IsDueIn(D("2026-11-02")));
        Assert.True(grocery.IsDueIn(D("2026-11-09")));
        Assert.Equal(D("2026-11-09"), grocery.NextDueFrom(D("2026-11-02")));

        grocery.SetSchedule(GroceryFrequency.Monthly, null);
        Assert.Null(grocery.StartWeek);

        Assert.Throws<ArgumentException>(() => grocery.SetSchedule(GroceryFrequency.EveryTwoWeeks, null));
        Assert.Throws<ArgumentException>(() => grocery.SetSchedule(GroceryFrequency.EveryTwoWeeks, D("2026-11-10")));
        Assert.Throws<ArgumentException>(() => grocery.SetSchedule(GroceryFrequency.Weekly, D("2026-11-09")));
        Assert.Throws<ArgumentOutOfRangeException>(() => grocery.SetSchedule((GroceryFrequency)99, null));
    }
}
