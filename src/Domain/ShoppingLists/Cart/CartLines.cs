namespace Foodplanner.Domain.ShoppingLists.Cart;

/// <summary>An item of the list as the cart fill sees it: id, name in the request language (or the manual label) and the checked flag.</summary>
public sealed record CartSourceItem(Guid ItemId, string Name, bool Checked, Guid? IngredientId = null);

/// <summary>One item to put in a provider cart: one pack of whatever the provider finds for <see cref="Name"/>.</summary>
public sealed record CartLine(Guid ItemId, string Name, Guid? IngredientId = null);

/// <summary>
/// Which items of a shopping list go to a provider cart (T-053): the unchecked items of the "To buy" section, by name. Checked
/// items, "Check you have" staples (not part of the input at all) and items without a name are left out. The order of the list is kept.
/// </summary>
public static class CartLineSelector
{
    public static IReadOnlyList<CartLine> Select(IEnumerable<CartSourceItem> mainItems)
    {
        ArgumentNullException.ThrowIfNull(mainItems);
        return [.. mainItems
            .Where(i => !i.Checked && !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => new CartLine(i.ItemId, i.Name.Trim(), i.IngredientId))];
    }
}
