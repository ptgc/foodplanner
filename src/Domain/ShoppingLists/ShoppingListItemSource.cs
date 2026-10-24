using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>A recipe that contributes to a generated item ("from recipe X"). Unique per (item, recipe). Goes with its item.</summary>
public sealed class ShoppingListItemSource : Entity, ITenantEntity
{
    private ShoppingListItemSource()
    {
    }

    public ShoppingListItemSource(Guid shoppingListItemId, Guid recipeId)
    {
        if (shoppingListItemId == Guid.Empty)
        {
            throw new ArgumentException("A source needs an item.", nameof(shoppingListItemId));
        }

        if (recipeId == Guid.Empty)
        {
            throw new ArgumentException("A source needs a recipe.", nameof(recipeId));
        }

        ShoppingListItemId = shoppingListItemId;
        RecipeId = recipeId;
    }

    public Guid HouseholdId { get; private set; }

    public Guid ShoppingListItemId { get; private set; }

    public Guid RecipeId { get; private set; }
}
