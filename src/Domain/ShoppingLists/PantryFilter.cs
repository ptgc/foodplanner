namespace Foodplanner.Domain.ShoppingLists;

/// <summary>An ingredient the household has confirmed, with its optional expiry. The pure view of a <see cref="PantryItem"/>.</summary>
public sealed record PantryEntry(Guid IngredientId, DateTimeOffset? ExpiresAt)
{
    public bool IsActive(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;
}

/// <summary>
/// Pure: applies the household's pantry to a generated list. Staples with an active pantry entry (not expired at
/// <c>now</c>) are removed from <see cref="GeneratedList.CheckYouHave"/>; everything else, including the order, is
/// unchanged. The pantry only applies to staples: the main list is never filtered.
/// </summary>
public static class PantryFilter
{
    public static GeneratedList Apply(GeneratedList list, IEnumerable<PantryEntry> pantry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(pantry);

        var have = pantry.Where(p => p.IsActive(now)).Select(p => p.IngredientId).ToHashSet();
        return have.Count == 0
            ? list
            : new GeneratedList(list.Items, [.. list.CheckYouHave.Where(s => !have.Contains(s.IngredientId))]);
    }
}
