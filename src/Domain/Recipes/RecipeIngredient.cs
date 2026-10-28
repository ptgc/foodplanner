using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Recipes;

/// <summary>
/// A structured ingredient line of a recipe (CLAUDE.md rule 5): ingredient id, quantity and unit id, never free text.
/// A line "to taste" has no quantity and no unit; otherwise it has both. The ingredient is a global catalog row or the
/// household's own; the unit is global. Whether the ids are visible to the household is checked by the endpoint before
/// saving (Q-012), not here. Ordered by <see cref="Position"/> (0-based).
/// </summary>
public sealed class RecipeIngredient : Entity, ITenantEntity
{
    public const int MaxNoteLength = 200;
    public const decimal MaxQuantity = 100000m;
    public const int MaxQuantityDecimals = 4;

    private RecipeIngredient()
    {
    }

    public RecipeIngredient(Guid recipeId, int position, Guid ingredientId, decimal? quantity, Guid? unitId, string? note)
    {
        if (recipeId == Guid.Empty)
        {
            throw new ArgumentException("An ingredient line needs a recipe.", nameof(recipeId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(position);
        if (ingredientId == Guid.Empty)
        {
            throw new ArgumentException("An ingredient line needs an ingredient.", nameof(ingredientId));
        }

        if (quantity is { } q && (q <= 0 || q > MaxQuantity || decimal.Round(q, MaxQuantityDecimals) != q))
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        if ((quantity is null) != (unitId is null) || unitId == Guid.Empty)
        {
            throw new ArgumentException("Quantity and unit go together: both, or neither for 'to taste'.", nameof(unitId));
        }

        if (note is { Length: > MaxNoteLength })
        {
            throw new ArgumentException("The note is too long.", nameof(note));
        }

        RecipeId = recipeId;
        Position = position;
        IngredientId = ingredientId;
        Quantity = quantity;
        UnitId = unitId;
        Note = note;
    }

    public Guid HouseholdId { get; private set; }

    public Guid RecipeId { get; private set; }

    public int Position { get; private set; }

    public Guid IngredientId { get; private set; }

    /// <summary>Greater than zero; null means "to taste".</summary>
    public decimal? Quantity { get; private set; }

    public Guid? UnitId { get; private set; }

    public string? Note { get; private set; }

    /// <summary>Points the line at another ingredient (merging two ingredients, T-079). Visibility of the id is the caller's check.</summary>
    public void ChangeIngredient(Guid ingredientId)
    {
        if (ingredientId == Guid.Empty)
        {
            throw new ArgumentException("An ingredient line needs an ingredient.", nameof(ingredientId));
        }

        IngredientId = ingredientId;
    }
}
