using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Recipes;

/// <summary>
/// Explicit join between a recipe and a tag (no implicit many-to-many between tenant entities, T-012). Unique per
/// (recipe, tag). The tag is global or the household's own; the endpoint resolves the id through the filtered set before
/// saving (Q-012). Goes with its recipe.
/// </summary>
public sealed class RecipeTag : Entity, ITenantEntity
{
    private RecipeTag()
    {
    }

    public RecipeTag(Guid recipeId, Guid tagId)
    {
        if (recipeId == Guid.Empty)
        {
            throw new ArgumentException("A recipe tag needs a recipe.", nameof(recipeId));
        }

        if (tagId == Guid.Empty)
        {
            throw new ArgumentException("A recipe tag needs a tag.", nameof(tagId));
        }

        RecipeId = recipeId;
        TagId = tagId;
    }

    public Guid HouseholdId { get; private set; }

    public Guid RecipeId { get; private set; }

    public Guid TagId { get; private set; }
}
