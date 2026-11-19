using Foodplanner.Domain.Common;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// One of a household's regular weekly groceries (bread, milk, ...), T-092: an ingredient (global or the household's own, so
/// it sorts by store section like everything else, CLAUDE.md rule 5) with an optional amount and unit (both or neither).
/// One row per household and ingredient (unique index). The button on the shopping list puts the set on a week's list.
/// HouseholdId is assigned by Infrastructure.
/// </summary>
public sealed class WeeklyGrocery : Entity, ITenantEntity
{
    /// <summary>The most weekly groceries one household can have.</summary>
    public const int MaxPerHousehold = 50;

    private WeeklyGrocery()
    {
    }

    public WeeklyGrocery(
        Guid ingredientId, decimal? amount, Guid? unitId, GroceryFrequency frequency = GroceryFrequency.Weekly, DateOnly? startWeek = null)
    {
        if (ingredientId == Guid.Empty)
        {
            throw new ArgumentException("A weekly grocery needs an ingredient.", nameof(ingredientId));
        }

        IngredientId = ingredientId;
        SetQuantity(amount, unitId);
        SetSchedule(frequency, startWeek);
    }

    public Guid HouseholdId { get; private set; }

    public Guid IngredientId { get; private set; }

    /// <summary>Null (with a null unit) = no amount: "just have it on the list".</summary>
    public decimal? Amount { get; private set; }

    public Guid? UnitId { get; private set; }

    /// <summary>How often it is due (T-109). Weekly for every row from before.</summary>
    public GroceryFrequency Frequency { get; private set; }

    /// <summary>
    /// The anchor week (a Monday) that "every second week" counts from. Set only for <see cref="GroceryFrequency.EveryTwoWeeks"/>;
    /// null for Weekly and Monthly, where it has no meaning (so old rows need no invented date).
    /// </summary>
    public DateOnly? StartWeek { get; private set; }

    /// <summary>Whether this grocery is due in the week that starts on <paramref name="weekStart"/> (a Monday).</summary>
    public bool IsDueIn(DateOnly weekStart) => GrocerySchedule.IsDue(Frequency, StartWeek, weekStart);

    /// <summary>The first due week from <paramref name="fromWeek"/> (a Monday) on.</summary>
    public DateOnly NextDueFrom(DateOnly fromWeek) => GrocerySchedule.NextDue(Frequency, StartWeek, fromWeek);

    /// <summary>
    /// Sets the frequency and anchor together: an anchor (a Monday) is required for every second week and must be null otherwise.
    /// </summary>
    public void SetSchedule(GroceryFrequency frequency, DateOnly? startWeek)
    {
        if (!Enum.IsDefined(frequency))
        {
            throw new ArgumentOutOfRangeException(nameof(frequency));
        }

        if ((frequency == GroceryFrequency.EveryTwoWeeks) != (startWeek is not null))
        {
            throw new ArgumentException("Only every second week has an anchor week, and it needs one.", nameof(startWeek));
        }

        if (startWeek is { } week && week.DayOfWeek != DayOfWeek.Monday)
        {
            throw new ArgumentException("The anchor week must be a Monday.", nameof(startWeek));
        }

        Frequency = frequency;
        StartWeek = startWeek;
    }

    /// <summary>Whether an amount is valid: more than 0, at most the recipe maximum, at most 4 decimals.</summary>
    public static bool IsValidAmount(decimal amount) =>
        amount > 0 && amount <= RecipeIngredient.MaxQuantity && decimal.Round(amount, RecipeIngredient.MaxQuantityDecimals) == amount;

    public void SetQuantity(decimal? amount, Guid? unitId)
    {
        if ((amount is null) != (unitId is null) || unitId == Guid.Empty)
        {
            throw new ArgumentException("Amount and unit go together: both, or neither for no amount.", nameof(unitId));
        }

        if (amount is { } a && !IsValidAmount(a))
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        Amount = amount;
        UnitId = unitId;
    }

    /// <summary>Points the row at another ingredient (merging two ingredients, T-079).</summary>
    public void ChangeIngredient(Guid ingredientId)
    {
        if (ingredientId == Guid.Empty)
        {
            throw new ArgumentException("A weekly grocery needs an ingredient.", nameof(ingredientId));
        }

        IngredientId = ingredientId;
    }
}
