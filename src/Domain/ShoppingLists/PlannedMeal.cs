using Foodplanner.Domain.Quantities;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>One ingredient line of a recipe, as the shopping list generator needs it. Quantity is for <see cref="PlannedMeal.RecipeServings"/>.</summary>
public sealed record PlannedIngredient
{
    public PlannedIngredient(Guid ingredientId, string ingredientName, bool isStaple, Quantity quantity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ingredientName);
        ArgumentNullException.ThrowIfNull(quantity);
        IngredientId = ingredientId;
        IngredientName = ingredientName;
        IsStaple = isStaple;
        Quantity = quantity;
    }

    public Guid IngredientId { get; }

    public string IngredientName { get; }

    public bool IsStaple { get; }

    public Quantity Quantity { get; }
}

/// <summary>A recipe planned for some number of servings. Both serving counts must be positive.</summary>
public sealed record PlannedMeal
{
    public PlannedMeal(Guid recipeId, int recipeServings, int plannedServings, IReadOnlyList<PlannedIngredient> ingredients)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recipeServings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plannedServings);
        ArgumentNullException.ThrowIfNull(ingredients);
        RecipeId = recipeId;
        RecipeServings = recipeServings;
        PlannedServings = plannedServings;
        Ingredients = ingredients;
    }

    public Guid RecipeId { get; init; }

    /// <summary>The servings the recipe's quantities are written for.</summary>
    public int RecipeServings { get; }

    /// <summary>The servings the plan wants.</summary>
    public int PlannedServings { get; }

    public IReadOnlyList<PlannedIngredient> Ingredients { get; }
}
