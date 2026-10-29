using Foodplanner.Domain.Quantities;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>A weekly grocery as the planner sees it: ingredient and optional amount/unit ids.</summary>
public sealed record WeeklyGrocerySpec(Guid IngredientId, decimal? Amount, Guid? UnitId);

/// <summary>What a list item holds of the weekly part now (T-092). <see cref="IsWeekly"/> false = no weekly part.</summary>
public sealed record WeeklyItemState(Guid ItemId, Guid IngredientId, bool IsWeekly, decimal? WeeklyAmount, Guid? WeeklyUnitId);

/// <summary>An existing item whose weekly part must be set to <paramref name="Grocery"/>'s amount.</summary>
public sealed record WeeklyItemUpdate(Guid ItemId, WeeklyGrocerySpec Grocery);

/// <summary>What pressing the button changes: new items, and existing items that gain or change their weekly part.</summary>
public sealed record WeeklyAddPlan(IReadOnlyList<WeeklyGrocerySpec> Add, IReadOnlyList<WeeklyItemUpdate> Update)
{
    public bool IsEmpty => Add.Count == 0 && Update.Count == 0;
}

/// <summary>A weekly part to lay over a generated list: the ingredient and its amount (null = no amount).</summary>
public sealed record WeeklyEntry(Guid IngredientId, Quantity? Quantity);

/// <summary>
/// Pure rules for the weekly groceries (T-092).
///  - <see cref="Plan"/> (the button): ensures every weekly grocery is on the week's list. An ingredient with no item is
///    added; an item without a weekly part, or with a different weekly amount (the set was edited), gets it; an item that
///    already has exactly this weekly part is left alone, so pressing twice changes nothing. An item the user removed is
///    simply missing again, so it comes back.
///  - <see cref="Overlay"/> (generation): lays the weekly parts over the list generated from the plan, so the plan
///    reconciliation never drops them. The weekly amount is merged into the item the recipes made (<see cref="QuantityMerger"/>:
///    same dimension adds up, otherwise it is an extra quantity line); an ingredient the plan does not need becomes an item of
///    its own; a staple the plan only wants checked moves to the main list, because the household asked for it by amount.
/// </summary>
public static class WeeklyGroceries
{
    public static WeeklyAddPlan Plan(IEnumerable<WeeklyGrocerySpec> groceries, IEnumerable<WeeklyItemState> items)
    {
        ArgumentNullException.ThrowIfNull(groceries);
        ArgumentNullException.ThrowIfNull(items);

        var byIngredient = items.GroupBy(i => i.IngredientId).ToDictionary(g => g.Key, g => g.First());
        var add = new List<WeeklyGrocerySpec>();
        var update = new List<WeeklyItemUpdate>();
        foreach (var grocery in groceries.GroupBy(g => g.IngredientId).Select(g => g.First()).OrderBy(g => g.IngredientId))
        {
            if (!byIngredient.TryGetValue(grocery.IngredientId, out var item))
            {
                add.Add(grocery);
            }
            else if (!item.IsWeekly || item.WeeklyAmount != grocery.Amount || item.WeeklyUnitId != grocery.UnitId)
            {
                update.Add(new WeeklyItemUpdate(item.ItemId, grocery));
            }
        }

        return new WeeklyAddPlan(add, update);
    }

    public static GeneratedList Overlay(GeneratedList generated, IEnumerable<WeeklyEntry> weekly, UnitCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(weekly);
        ArgumentNullException.ThrowIfNull(catalog);

        var entries = weekly.GroupBy(w => w.IngredientId).Select(g => g.First()).OrderBy(w => w.IngredientId).ToList();
        if (entries.Count == 0)
        {
            return generated;
        }

        var items = generated.Items.ToList();
        var staples = generated.CheckYouHave.ToList();
        foreach (var entry in entries)
        {
            IReadOnlyList<Quantity> amount = entry.Quantity is { Amount: not null } q ? [q] : [];
            var index = items.FindIndex(i => i.IngredientId == entry.IngredientId);
            if (index >= 0)
            {
                if (amount.Count > 0)
                {
                    items[index] = items[index] with { Quantities = QuantityMerger.MergeForDisplay([.. items[index].Quantities, .. amount], catalog) };
                }

                continue;
            }

            var stapleIndex = staples.FindIndex(s => s.IngredientId == entry.IngredientId);
            var name = stapleIndex >= 0 ? staples[stapleIndex].IngredientName : string.Empty;
            IReadOnlyList<Guid> recipes = stapleIndex >= 0 ? staples[stapleIndex].RecipeIds : [];
            if (stapleIndex >= 0)
            {
                staples.RemoveAt(stapleIndex);
            }

            items.Add(new GeneratedItem(entry.IngredientId, name, amount.Count > 0 ? QuantityMerger.MergeForDisplay(amount, catalog) : [], recipes));
        }

        return new GeneratedList(items, staples);
    }
}
