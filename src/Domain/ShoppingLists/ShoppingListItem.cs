using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// One line of a shopping list. Either generated from the plan (<see cref="IngredientId"/> set, no label; the quantities
/// are <see cref="ShoppingListItemQuantity"/> rows and the contributing recipes <see cref="ShoppingListItemSource"/> rows)
/// or manual (a free-text <see cref="Label"/>, no ingredient). Free text exists only on manual items (CLAUDE.md rule 5).
/// <see cref="RowVersion"/> is the optimistic concurrency token of the check-off (see docs/domain.md, T-033).
/// HouseholdId is assigned by Infrastructure.
/// </summary>
public sealed class ShoppingListItem : Entity, ITenantEntity
{
    public const int MaxLabelLength = 100;

    private ShoppingListItem()
    {
    }

    private ShoppingListItem(Guid shoppingListId, ShoppingSection section, Guid? ingredientId, string? label)
    {
        if (shoppingListId == Guid.Empty)
        {
            throw new ArgumentException("An item needs a list.", nameof(shoppingListId));
        }

        if (!Enum.IsDefined(section))
        {
            throw new ArgumentOutOfRangeException(nameof(section));
        }

        ShoppingListId = shoppingListId;
        Section = section;
        IngredientId = ingredientId;
        Label = label;
    }

    public Guid HouseholdId { get; private set; }

    public Guid ShoppingListId { get; private set; }

    public ShoppingSection Section { get; private set; }

    /// <summary>The ingredient of a generated item; null for a manual item.</summary>
    public Guid? IngredientId { get; private set; }

    /// <summary>The text of a manual item; null for a generated item.</summary>
    public string? Label { get; private set; }

    public bool IsManual => IngredientId is null;

    public bool Checked { get; private set; }

    public Guid? CheckedByUserId { get; private set; }

    public DateTimeOffset? CheckedAt { get; private set; }

    /// <summary>
    /// The item was checked and a later regeneration changed its quantities, so what was bought may no longer be enough.
    /// Cleared when the item is unchecked or checked again.
    /// </summary>
    public bool QuantityChangedSinceChecked { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// The household's weekly groceries (T-092) put this ingredient on the list. The weekly part is kept here (amount and unit
    /// both set, or both null for "no amount") and laid over the plan's quantities whenever the list is generated, so the plan
    /// reconciliation never drops it. Only a generated item (with an ingredient) can have one.
    /// </summary>
    public bool IsWeekly { get; private set; }

    public decimal? WeeklyAmount { get; private set; }

    public Guid? WeeklyUnitId { get; private set; }

    /// <summary>Sets the weekly part (replacing an earlier one). Returns true when something changed.</summary>
    public bool SetWeekly(decimal? amount, Guid? unitId)
    {
        if (IngredientId is null)
        {
            throw new InvalidOperationException("A manual item cannot be a weekly grocery.");
        }

        if ((amount is null) != (unitId is null) || unitId == Guid.Empty || (amount is { } a && !WeeklyGrocery.IsValidAmount(a)))
        {
            throw new ArgumentException("Amount and unit go together, and the amount must be valid.", nameof(amount));
        }

        if (IsWeekly && WeeklyAmount == amount && WeeklyUnitId == unitId)
        {
            return false;
        }

        IsWeekly = true;
        WeeklyAmount = amount;
        WeeklyUnitId = unitId;
        return true;
    }

    /// <summary>Drops the weekly part (the item may stay when the plan needs it). Returns true when there was one.</summary>
    public bool ClearWeekly()
    {
        if (!IsWeekly)
        {
            return false;
        }

        IsWeekly = false;
        WeeklyAmount = null;
        WeeklyUnitId = null;
        return true;
    }

    /// <summary>Points a generated item at another ingredient (merging two ingredients, T-079). The next regeneration reconciles section and quantities.</summary>
    public void ChangeIngredient(Guid ingredientId)
    {
        if (IngredientId is null)
        {
            throw new InvalidOperationException("A manual item has no ingredient.");
        }

        if (ingredientId == Guid.Empty)
        {
            throw new ArgumentException("A generated item needs an ingredient.", nameof(ingredientId));
        }

        IngredientId = ingredientId;
    }

    public static ShoppingListItem Generated(Guid shoppingListId, Guid ingredientId, ShoppingSection section)
    {
        if (ingredientId == Guid.Empty)
        {
            throw new ArgumentException("A generated item needs an ingredient.", nameof(ingredientId));
        }

        return new ShoppingListItem(shoppingListId, section, ingredientId, null);
    }

    /// <summary>An item made by the weekly groceries button: main list, ingredient, weekly part set.</summary>
    public static ShoppingListItem Weekly(Guid shoppingListId, Guid ingredientId, decimal? amount, Guid? unitId)
    {
        var item = Generated(shoppingListId, ingredientId, ShoppingSection.Main);
        item.SetWeekly(amount, unitId);
        return item;
    }

    public static ShoppingListItem Manual(Guid shoppingListId, string label)
    {
        var normalized = NormalizeLabel(label) ?? throw new ArgumentException("Invalid label.", nameof(label));
        return new ShoppingListItem(shoppingListId, ShoppingSection.Main, null, normalized);
    }

    /// <summary>Trims and collapses inner whitespace; null when empty, too long (over <see cref="MaxLabelLength"/>) or containing control characters.</summary>
    public static string? NormalizeLabel(string? label)
    {
        if (label is null)
        {
            return null;
        }

        var collapsed = string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length is 0 or > MaxLabelLength || collapsed.Any(char.IsControl) ? null : collapsed;
    }

    /// <summary>
    /// Sets the checked state. Idempotent: checking an item that is already checked keeps who checked it first and
    /// when, and only clears the quantity-changed flag (checking again acknowledges the change). Unchecking clears
    /// everything. Returns true when something changed.
    /// </summary>
    public bool SetChecked(bool isChecked, Guid userId, DateTimeOffset now)
    {
        if (isChecked)
        {
            if (!Checked)
            {
                Checked = true;
                CheckedByUserId = userId;
                CheckedAt = now;
                QuantityChangedSinceChecked = false;
                return true;
            }

            var changed = QuantityChangedSinceChecked;
            QuantityChangedSinceChecked = false;
            return changed;
        }

        if (!Checked && !QuantityChangedSinceChecked)
        {
            return false;
        }

        Checked = false;
        CheckedByUserId = null;
        CheckedAt = null;
        QuantityChangedSinceChecked = false;
        return true;
    }

    /// <summary>Regeneration found the quantities changed: remember it when the item is already checked.</summary>
    public void QuantitiesChanged()
    {
        if (Checked)
        {
            QuantityChangedSinceChecked = true;
        }
    }

    /// <summary>Regeneration moved the ingredient to the other section (its staple flag changed). The item starts over unchecked.</summary>
    public void MoveTo(ShoppingSection section)
    {
        if (!Enum.IsDefined(section))
        {
            throw new ArgumentOutOfRangeException(nameof(section));
        }

        Section = section;
        Checked = false;
        CheckedByUserId = null;
        CheckedAt = null;
        QuantityChangedSinceChecked = false;
    }
}
