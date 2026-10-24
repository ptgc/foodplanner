using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.MealPlans;

/// <summary>
/// A household's plan for one ISO-8601 week, identified by the week's Monday (<see cref="WeekStart"/>). One per household
/// and week (unique index). Its entries (<see cref="MealPlanEntry"/>) point to it by id. HouseholdId is assigned by
/// Infrastructure when the plan is added.
/// </summary>
public sealed class MealPlan : Entity, ITenantEntity
{
    public const int DaysInWeek = 7;

    private MealPlan()
    {
    }

    public MealPlan(DateOnly weekStart)
    {
        if (!IsWeekStart(weekStart))
        {
            throw new ArgumentException("A plan starts on a Monday.", nameof(weekStart));
        }

        WeekStart = weekStart;
    }

    public Guid HouseholdId { get; private set; }

    public DateOnly WeekStart { get; private set; }

    public static bool IsWeekStart(DateOnly date) => date.DayOfWeek == DayOfWeek.Monday;

    /// <summary>True when <paramref name="date"/> is one of the seven days Monday to Sunday of the week starting <paramref name="weekStart"/>.</summary>
    public static bool IsInWeek(DateOnly weekStart, DateOnly date) =>
        date >= weekStart && date <= weekStart.AddDays(DaysInWeek - 1);
}
