using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Recipes;

/// <summary>
/// An ingredient that can stand in for the line's own ("margarin eller olje": the line's ingredient is the first, these
/// are the others). Always a catalog or own ingredient, never free text (CLAUDE.md rule 5). A separate tenant row so the
/// save-time reference check (T-073) covers the id; it points to its line only (no second cascade path from the recipe)
/// and goes with it. Ordered by <see cref="Position"/> (0-based).
/// </summary>
public sealed class RecipeIngredientAlternative : Entity, ITenantEntity
{
    public const int MaxPerLine = 5;

    private RecipeIngredientAlternative()
    {
    }

    public RecipeIngredientAlternative(Guid recipeIngredientId, int position, Guid ingredientId)
    {
        if (recipeIngredientId == Guid.Empty)
        {
            throw new ArgumentException("An alternative needs an ingredient line.", nameof(recipeIngredientId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(position);
        if (ingredientId == Guid.Empty)
        {
            throw new ArgumentException("An alternative needs an ingredient.", nameof(ingredientId));
        }

        RecipeIngredientId = recipeIngredientId;
        Position = position;
        IngredientId = ingredientId;
    }

    /// <summary>Points the alternative at another ingredient (merging two ingredients, T-079). Visibility of the id is the caller's check.</summary>
    public void ChangeIngredient(Guid ingredientId)
    {
        if (ingredientId == Guid.Empty)
        {
            throw new ArgumentException("An alternative needs an ingredient.", nameof(ingredientId));
        }

        IngredientId = ingredientId;
    }

    public Guid HouseholdId { get; private set; }

    public Guid RecipeIngredientId { get; private set; }

    public int Position { get; private set; }

    public Guid IngredientId { get; private set; }
}
