using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Quantities;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// What the household has of one ingredient, with its optional expiry and optional amount. The pure view of a
/// <see cref="PantryItem"/>. <see cref="Amount"/> null (or a quantity with no amount) = "we have some", no amount known.
/// </summary>
public sealed record PantryEntry(Guid IngredientId, DateTimeOffset? ExpiresAt, Quantity? Amount = null)
{
    public bool IsActive(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;
}

/// <summary>
/// Pure: subtracts the household's pantry from a generated list (T-037, T-093). Only entries that are active at
/// <c>now</c> count (no expiry, or <see cref="PantryEntry.ExpiresAt"/> after <c>now</c>); an expired entry is ignored.
///  - A staple (or any "check you have" line without an amount) with an active entry is omitted: the household has it, as
///    in T-037.
///  - An item on the main list with an active entry is subtracted:
///      * the entry has no amount ("we have some"), the item has no amount to subtract from (a weekly grocery without an
///        amount), or no need line is in the entry's dimension (kg of cheese needed, 2 dl at home; count units only match
///        the same unit): nothing is bought, the item moves to "check you have" with the pantry note and the need;
///      * a need line in the entry's dimension (Mass with Mass, Volume with Volume, Count with the same count unit, compared in
///        the base unit) that the entry covers (have is at least need) is dropped; one it only partly covers becomes need minus
///        have, in the line's unit, rounded for display (<see cref="QuantityMerger.MergeForDisplay"/>), and the item stays on
///        the main list with the pantry note;
///      * if no measured line is left to buy, the item moves to "check you have" with the original need.
///    A line in another dimension than the entry's stays to buy in full when another line of the item was matched.
/// The pantry is never reduced here (no deduction after cooking or shopping, T-094). Order: both sections keep the generator's
/// order (name, then id).
/// </summary>
public static class PantryFilter
{
    public static GeneratedList Apply(GeneratedList list, IEnumerable<PantryEntry> pantry, DateTimeOffset now, UnitCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(pantry);
        ArgumentNullException.ThrowIfNull(catalog);

        var have = pantry.Where(p => p.IsActive(now)).GroupBy(p => p.IngredientId).ToDictionary(g => g.Key, g => g.First());
        if (have.Count == 0)
        {
            return list;
        }

        var items = new List<GeneratedItem>();
        var check = new List<GeneratedStaple>(list.CheckYouHave.Where(s => !have.ContainsKey(s.IngredientId)));
        var moved = false;
        foreach (var item in list.Items)
        {
            if (!have.TryGetValue(item.IngredientId, out var entry))
            {
                items.Add(item);
                continue;
            }

            var (remaining, atHome) = Subtract(item.Quantities, entry, catalog);
            if (remaining.Count == 0)
            {
                check.Add(new GeneratedStaple(item.IngredientId, item.IngredientName, item.RecipeIds, item.Quantities, atHome));
                moved = true;
            }
            else
            {
                items.Add(item with { Quantities = remaining, AtHome = atHome });
            }
        }

        if (moved)
        {
            check = [.. check
                .OrderBy(s => s.IngredientName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.IngredientName, StringComparer.Ordinal)
                .ThenBy(s => s.IngredientId)];
        }

        return new GeneratedList(items, check);
    }

    /// <summary>What is still to buy of <paramref name="need"/> (empty = nothing: covered or "check"), and the note.</summary>
    private static (IReadOnlyList<Quantity> Remaining, AtHome AtHome) Subtract(IReadOnlyList<Quantity> need, PantryEntry entry, UnitCatalog catalog)
    {
        var have = entry.Amount is { Amount: not null } ? entry.Amount : null;
        var atHome = new AtHome(have);
        var measured = need.Where(q => q.Amount is not null).ToList();
        if (have is null || measured.Count == 0)
        {
            return ([], atHome);
        }

        var remaining = new List<Quantity>();
        var matched = false;
        foreach (var line in need)
        {
            if (line.Amount is null || !UnitConversion.CanConvert(line.Unit, have.Unit))
            {
                remaining.Add(line);
                continue;
            }

            matched = true;
            var haveBase = have.BaseAmount!.Value;
            var needBase = line.BaseAmount!.Value;
            if (haveBase < needBase)
            {
                remaining.Add(new Quantity((needBase - haveBase) / line.Unit.FactorToBase, line.Unit));
            }
        }

        // Nothing in the entry's dimension: have some, check; nothing is bought for it.
        if (!matched || !remaining.Any(q => q.Amount is not null))
        {
            return ([], atHome);
        }

        return (QuantityMerger.MergeForDisplay(remaining, catalog), atHome);
    }
}
