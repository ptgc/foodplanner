using Foodplanner.Domain.Quantities;

namespace Foodplanner.Domain.Recipes;

/// <summary>
/// Scaling a recipe to another number of servings (domain.md: "Scaling recipe servings scales all ingredient quantities
/// proportionally"). The one implementation, used by the shopping list generator and by the recipe scaling endpoint.
/// </summary>
public static class RecipeScaling
{
    public const int MinServings = 1;
    public const int MaxServings = 100;

    /// <summary>Exact scaling: amount * servings / baseServings. To-taste (no amount) is unchanged. No rounding.</summary>
    public static Quantity Scale(Quantity quantity, int baseServings, int servings)
    {
        ArgumentNullException.ThrowIfNull(quantity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseServings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(servings);
        return quantity.Amount is { } amount ? new Quantity(amount * servings / baseServings, quantity.Unit) : quantity;
    }

    /// <summary>
    /// Scales a line by its kind (T-076): only a Measured quantity scales. ToTaste and AsNeeded are not amounts, so they
    /// are returned unchanged for any number of servings.
    /// </summary>
    public static Quantity Scale(AmountKind kind, Quantity quantity, int baseServings, int servings) =>
        kind == AmountKind.Measured ? Scale(quantity, baseServings, servings) : quantity;

    /// <summary>Scales exactly, then rounds and picks the display unit (<see cref="QuantityMerger.ForDisplay"/>).</summary>
    public static Quantity ScaleForDisplay(Quantity quantity, int baseServings, int servings, UnitCatalog catalog) =>
        QuantityMerger.ForDisplay([Scale(quantity, baseServings, servings)], catalog)[0];
}
