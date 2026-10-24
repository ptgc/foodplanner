using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// One quantity line of an item, structured (CLAUDE.md rule 5): amount and unit id. A generated item has the merged
/// display lines of the generator (a mass line and a volume line stay separate); a manual item has at most one.
/// Amount and unit are both null for "to taste". Ordered by <see cref="Position"/> (0-based). Goes with its item.
/// </summary>
public sealed class ShoppingListItemQuantity : Entity, ITenantEntity
{
    private ShoppingListItemQuantity()
    {
    }

    public ShoppingListItemQuantity(Guid shoppingListItemId, int position, decimal? amount, Guid? unitId)
    {
        if (shoppingListItemId == Guid.Empty)
        {
            throw new ArgumentException("A quantity needs an item.", nameof(shoppingListItemId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(position);
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        if ((amount is null) != (unitId is null) || unitId == Guid.Empty)
        {
            throw new ArgumentException("Amount and unit go together: both, or neither for 'to taste'.", nameof(unitId));
        }

        ShoppingListItemId = shoppingListItemId;
        Position = position;
        Amount = amount;
        UnitId = unitId;
    }

    public Guid HouseholdId { get; private set; }

    public Guid ShoppingListItemId { get; private set; }

    public int Position { get; private set; }

    public decimal? Amount { get; private set; }

    public Guid? UnitId { get; private set; }
}
