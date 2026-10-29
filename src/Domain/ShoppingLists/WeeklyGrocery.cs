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

    public WeeklyGrocery(Guid ingredientId, decimal? amount, Guid? unitId)
    {
        if (ingredientId == Guid.Empty)
        {
            throw new ArgumentException("A weekly grocery needs an ingredient.", nameof(ingredientId));
        }

        IngredientId = ingredientId;
        SetQuantity(amount, unitId);
    }

    public Guid HouseholdId { get; private set; }

    public Guid IngredientId { get; private set; }

    /// <summary>Null (with a null unit) = no amount: "just have it on the list".</summary>
    public decimal? Amount { get; private set; }

    public Guid? UnitId { get; private set; }

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
