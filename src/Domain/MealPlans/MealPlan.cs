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

    /// <summary>When an Adult locked the week (T-026), or null. A locked week cannot be changed by a Child or a kid profile.</summary>
    public DateTimeOffset? LockedAt { get; private set; }

    /// <summary>Who locked the week. Set together with <see cref="LockedAt"/>.</summary>
    public Guid? LockedByUserId { get; private set; }

    public bool IsLocked => LockedAt is not null;

    /// <summary>Locks the week. Locking a locked week changes nothing: the first locker and time stay.</summary>
    public void Lock(DateTimeOffset now, Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A lock needs the user who made it.", nameof(userId));
        }

        if (IsLocked)
        {
            return;
        }

        LockedAt = now;
        LockedByUserId = userId;
    }

    public void Unlock()
    {
        LockedAt = null;
        LockedByUserId = null;
    }

    public static bool IsWeekStart(DateOnly date) => date.DayOfWeek == DayOfWeek.Monday;

    /// <summary>True when <paramref name="date"/> is one of the seven days Monday to Sunday of the week starting <paramref name="weekStart"/>.</summary>
    public static bool IsInWeek(DateOnly weekStart, DateOnly date) =>
        date >= weekStart && date <= weekStart.AddDays(DaysInWeek - 1);
}
