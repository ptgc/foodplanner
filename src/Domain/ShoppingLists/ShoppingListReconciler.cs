using Foodplanner.Domain.Quantities;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>A stored quantity line, as ids: amount and unit are both null for "to taste".</summary>
public sealed record StoredQuantity(decimal? Amount, Guid? UnitId);

/// <summary>What the pantry held when the item was generated (T-093): amount and unit are both null for "we have some".</summary>
public sealed record StoredAtHome(decimal? Amount, Guid? UnitId);

/// <summary>A new value for the "at home" note of an item; <see cref="Value"/> null = not in the pantry (any more).</summary>
public sealed record AtHomeChange(StoredAtHome? Value);

/// <summary>A generated item as it is stored now.</summary>
public sealed record ExistingGeneratedItem(
    Guid ItemId,
    Guid IngredientId,
    ShoppingSection Section,
    bool Checked,
    IReadOnlyList<StoredQuantity> Quantities,
    IReadOnlyList<Guid> RecipeIds,
    StoredAtHome? AtHome = null);

/// <summary>A generated item the list does not have yet.</summary>
public sealed record ItemToAdd(
    Guid IngredientId,
    ShoppingSection Section,
    IReadOnlyList<StoredQuantity> Quantities,
    IReadOnlyList<Guid> RecipeIds,
    StoredAtHome? AtHome = null);

/// <summary>
/// A stored generated item that must change. <see cref="Quantities"/> and <see cref="RecipeIds"/> are the new full
/// contents, or null when unchanged. <see cref="MoveToSection"/> is set when the item changes section (it then starts
/// unchecked); <see cref="FlagQuantityChange"/> when it stays checked while its quantities changed. <see cref="AtHome"/> is
/// null when the pantry note is unchanged (T-093); a change of the note alone never flags the item.
/// </summary>
public sealed record ItemToUpdate(
    Guid ItemId,
    ShoppingSection? MoveToSection,
    bool FlagQuantityChange,
    IReadOnlyList<StoredQuantity>? Quantities,
    IReadOnlyList<Guid>? RecipeIds,
    AtHomeChange? AtHome = null);

public sealed record Reconciliation(
    IReadOnlyList<ItemToAdd> Add, IReadOnlyList<ItemToUpdate> Update, IReadOnlyList<Guid> RemoveItemIds)
{
    public bool IsEmpty => Add.Count == 0 && Update.Count == 0 && RemoveItemIds.Count == 0;
}

/// <summary>
/// Pure comparison of the stored generated items with a fresh <see cref="GeneratedList"/> (T-033). Generated items are
/// keyed by ingredient id alone (an ingredient is in exactly one section at a time). Rules:
///  - new ingredient: add; ingredient no longer in the plan: remove (checked or not);
///  - same ingredient: keep the item, so its checked state, checker and time survive; replace the quantities and
///    recipes when they differ;
///  - quantities changed on a checked item: it stays checked and is flagged (QuantityChangedSinceChecked);
///  - section changed (the ingredient became or stopped being a staple, or the pantry now covers it, T-093): the item is
///    moved and starts unchecked;
///  - the pantry note ("at home", T-093) is replaced when it differs. A covered item in "check you have" keeps the quantities
///    the plan needs, so the note can say "300 g at home (need 200 g)".
/// Identical input gives an empty result, so regenerating twice changes nothing (idempotent). Manual items are not part
/// of the input and are never touched.
/// </summary>
public static class ShoppingListReconciler
{
    public static Reconciliation Reconcile(IEnumerable<ExistingGeneratedItem> existing, GeneratedList generated)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(generated);

        var wanted = new List<ItemToAdd>();
        foreach (var item in generated.Items)
        {
            wanted.Add(new ItemToAdd(
                item.IngredientId, ShoppingSection.Main, [.. item.Quantities.Select(ToStored)], item.RecipeIds, ToStored(item.AtHome)));
        }

        foreach (var staple in generated.CheckYouHave)
        {
            wanted.Add(new ItemToAdd(
                staple.IngredientId, ShoppingSection.CheckYouHave, [.. staple.Needed.Select(ToStored)], staple.RecipeIds, ToStored(staple.AtHome)));
        }

        var current = existing.ToDictionary(e => e.IngredientId);
        var add = new List<ItemToAdd>();
        var update = new List<ItemToUpdate>();
        foreach (var target in wanted)
        {
            if (!current.TryGetValue(target.IngredientId, out var stored))
            {
                add.Add(target);
                continue;
            }

            var sectionChanged = stored.Section != target.Section;
            var quantitiesChanged = !stored.Quantities.SequenceEqual(target.Quantities);
            var recipesChanged = !stored.RecipeIds.Order().SequenceEqual(target.RecipeIds.Order());
            var atHomeChanged = stored.AtHome != target.AtHome;
            if (!sectionChanged && !quantitiesChanged && !recipesChanged && !atHomeChanged)
            {
                continue;
            }

            update.Add(new ItemToUpdate(
                stored.ItemId,
                sectionChanged ? target.Section : null,
                FlagQuantityChange: !sectionChanged && quantitiesChanged && stored.Checked,
                quantitiesChanged ? target.Quantities : null,
                recipesChanged ? target.RecipeIds : null,
                atHomeChanged ? new AtHomeChange(target.AtHome) : null));
        }

        var keep = wanted.Select(w => w.IngredientId).ToHashSet();
        var remove = current.Values.Where(e => !keep.Contains(e.IngredientId)).Select(e => e.ItemId).Order().ToList();
        return new Reconciliation(add, update, remove);
    }

    private static StoredQuantity ToStored(Quantity quantity) =>
        quantity.Amount is { } amount ? new StoredQuantity(amount, quantity.Unit.Id) : new StoredQuantity(null, null);

    private static StoredAtHome? ToStored(AtHome? atHome) =>
        atHome is null ? null : atHome.Amount is { Amount: { } amount } q ? new StoredAtHome(amount, q.Unit.Id) : new StoredAtHome(null, null);
}
