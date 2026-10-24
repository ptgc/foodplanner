using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Recipes;

/// <summary>One step of a recipe, ordered by <see cref="Position"/> (0-based). Goes with its recipe.</summary>
public sealed class RecipeStep : Entity, ITenantEntity
{
    public const int MaxTextLength = 2000;

    private RecipeStep()
    {
    }

    public RecipeStep(Guid recipeId, int position, string text)
    {
        if (recipeId == Guid.Empty)
        {
            throw new ArgumentException("A step needs a recipe.", nameof(recipeId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(position);
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
        {
            throw new ArgumentException("Invalid step text.", nameof(text));
        }

        RecipeId = recipeId;
        Position = position;
        Text = text;
    }

    public Guid HouseholdId { get; private set; }

    public Guid RecipeId { get; private set; }

    public int Position { get; private set; }

    public string Text { get; private set; } = string.Empty;
}
