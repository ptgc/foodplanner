namespace Foodplanner.Domain.ShoppingLists.Sync;

/// <summary>One item of the bulk save (T-130): the ingredient, the Oda product id, and whether an existing choice may be replaced.</summary>
public sealed record BulkPreferenceItem(Guid IngredientId, long OdaProductId, bool Replace);

/// <summary>
/// What a bulk save does (T-130). <see cref="Created"/> and <see cref="Replaced"/> are written, <see cref="Unchanged"/> items hold the stored id
/// already, <see cref="SkippedExisting"/> are ingredients with a choice whose item did not say <c>replace</c>.
/// <see cref="RowCountAfter"/> counts the household's rows after the call.
/// </summary>
public sealed record BulkPreferencePlan(
    IReadOnlyList<BulkPreferenceItem> Created,
    IReadOnlyList<BulkPreferenceItem> Replaced,
    IReadOnlyList<Guid> Unchanged,
    IReadOnlyList<Guid> SkippedExisting,
    int RowCountAfter)
{
    public bool ExceedsLimit => RowCountAfter > PreferredOdaProduct.MaxPerHousehold;
}

/// <summary>
/// Never overwrite unless ticked (T-130): an item for an ingredient that already has a choice is applied only with <c>Replace</c>; a new
/// ingredient is created whatever <c>Replace</c> says; the stored id again is a no-op, whatever <c>Replace</c> says.
/// </summary>
public static class BulkPreferencePlanner
{
    public static BulkPreferencePlan Plan(IReadOnlyDictionary<Guid, long> existing, IReadOnlyList<BulkPreferenceItem> items)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(items);
        var created = new List<BulkPreferenceItem>();
        var replaced = new List<BulkPreferenceItem>();
        var unchanged = new List<Guid>();
        var skipped = new List<Guid>();
        foreach (var item in items)
        {
            if (!existing.TryGetValue(item.IngredientId, out var stored))
            {
                created.Add(item);
            }
            else if (stored == item.OdaProductId)
            {
                unchanged.Add(item.IngredientId);
            }
            else if (item.Replace)
            {
                replaced.Add(item);
            }
            else
            {
                skipped.Add(item.IngredientId);
            }
        }

        return new BulkPreferencePlan(created, replaced, unchanged, skipped, existing.Count + created.Count);
    }
}
