using Foodplanner.Domain.Common;
using Foodplanner.Domain.MealPlans;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// A household's shopping list for one ISO-8601 week (the same week key as the <see cref="MealPlan"/>, the Monday).
/// One per household and week (unique index). Its items point to it by id. HouseholdId is assigned by Infrastructure.
/// </summary>
public sealed class ShoppingList : Entity, ITenantEntity
{
    private ShoppingList()
    {
    }

    public ShoppingList(DateOnly weekStart)
    {
        if (!MealPlan.IsWeekStart(weekStart))
        {
            throw new ArgumentException("A list starts on a Monday.", nameof(weekStart));
        }

        WeekStart = weekStart;
    }

    public Guid HouseholdId { get; private set; }

    public DateOnly WeekStart { get; private set; }
}
