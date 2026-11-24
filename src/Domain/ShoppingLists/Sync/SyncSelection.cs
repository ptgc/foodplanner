namespace Foodplanner.Domain.ShoppingLists.Sync;

/// <summary>
/// An ingredient that is used by at least one recipe of the household (T-130): its name in the request language, how many recipes use
/// it, whether it is a staple and whether the household already has an Oda choice for it.
/// </summary>
public sealed record SyncIngredient(Guid IngredientId, string Name, int RecipeCount, bool IsStaple, bool HasChoice);

/// <summary>The ingredients one run checks (in order), and how many matching ingredients are left after them.</summary>
public sealed record SyncSelection(IReadOnlyList<SyncIngredient> Selected, int NotCheckedCount);

/// <summary>
/// Which ingredients "Sync with Oda" checks (T-130): staples only with <c>includeStaples</c>, ingredients with a choice only with
/// <c>includeExisting</c>; most used first, then name (the request language's ordering), then id, so that <c>offset</c> pages the same
/// list without overlap or gaps. One run takes at most <c>max</c> ingredients starting at <c>offset</c>.
/// </summary>
public static class SyncSelector
{
    public static SyncSelection Select(
        IEnumerable<SyncIngredient> candidates, bool includeStaples, bool includeExisting, int offset, int max, IComparer<string> nameOrder)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(nameOrder);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);

        var ordered = candidates
            .Where(c => c.RecipeCount > 0 && (includeStaples || !c.IsStaple) && (includeExisting || !c.HasChoice))
            .OrderByDescending(c => c.RecipeCount)
            .ThenBy(c => c.Name, nameOrder)
            .ThenBy(c => c.IngredientId)
            .ToList();
        var page = ordered.Skip(offset).Take(max).ToList();
        return new SyncSelection(page, Math.Max(0, ordered.Count - offset - page.Count));
    }
}
