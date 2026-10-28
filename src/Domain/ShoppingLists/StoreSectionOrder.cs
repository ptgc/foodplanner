namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// The order the store sections are walked in (Q-019). <see cref="Default"/> is the fixed order; the functions also take
/// an optional custom order (one list per store, T-087/T-086). A section a custom order does not name sorts after the
/// named ones, in default order, so a partial or stale custom order still sorts every item.
/// </summary>
public static class StoreSectionOrder
{
    /// <summary>Fruit and vegetables, chilled counter, frozen, chilled dairy shelf, dry goods, then other.</summary>
    public static IReadOnlyList<StoreSection> Default { get; } =
    [
        StoreSection.FruitAndVegetables,
        StoreSection.MeatFishAndCheese,
        StoreSection.Frozen,
        StoreSection.Dairy,
        StoreSection.DryGoods,
        StoreSection.Other,
    ];

    /// <summary>The position of a section: its index in <paramref name="custom"/> when named there, else after those, in default order.</summary>
    public static int Rank(StoreSection section, IReadOnlyList<StoreSection>? custom = null)
    {
        var named = 0;
        if (custom is not null)
        {
            for (var i = 0; i < custom.Count; i++)
            {
                if (custom[i] == section)
                {
                    return i;
                }
            }

            named = custom.Count;
        }

        for (var i = 0; i < Default.Count; i++)
        {
            if (Default[i] == section)
            {
                return named + i;
            }
        }

        // An undefined value sorts with Other, last.
        return named + Default.Count - 1;
    }

    /// <summary>Orders by section, then by name with the given comparer, then by a stable id so the order is deterministic.</summary>
    public static IOrderedEnumerable<T> Sort<T>(
        IEnumerable<T> items, Func<T, StoreSection> section, Func<T, string> name, IComparer<string> nameComparer,
        Func<T, Guid> tieBreaker, IReadOnlyList<StoreSection>? custom = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items
            .OrderBy(i => Rank(section(i), custom))
            .ThenBy(name, nameComparer)
            .ThenBy(tieBreaker);
    }
}
