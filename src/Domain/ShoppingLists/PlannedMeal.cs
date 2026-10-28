using Foodplanner.Domain.Quantities;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// One ingredient line of a recipe, as the shopping list generator needs it. Quantity is for
/// <see cref="PlannedMeal.RecipeServings"/>. A null <c>kind</c> infers it from the quantity (an amount is Measured, none
/// is ToTaste); a ToTaste or AsNeeded line has an amount-less quantity.
/// </summary>
public sealed record PlannedIngredient
{
    public PlannedIngredient(
        Guid ingredientId,
        string ingredientName,
        bool isStaple,
        Quantity quantity,
        AmountKind? kind = null,
        IngredientPurpose purpose = IngredientPurpose.Main,
        bool isOptional = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ingredientName);
        ArgumentNullException.ThrowIfNull(quantity);
        var resolved = kind ?? AmountRules.Infer(quantity.Amount);
        if ((resolved == AmountKind.Measured) != (quantity.Amount is not null))
        {
            throw new ArgumentException("Only a Measured line has an amount.", nameof(quantity));
        }

        IngredientId = ingredientId;
        IngredientName = ingredientName;
        IsStaple = isStaple;
        Quantity = quantity;
        Kind = resolved;
        Purpose = purpose;
        IsOptional = isOptional;
    }

    public Guid IngredientId { get; }

    public string IngredientName { get; }

    public bool IsStaple { get; }

    public Quantity Quantity { get; }

    public AmountKind Kind { get; }

    public IngredientPurpose Purpose { get; }

    public bool IsOptional { get; }
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
