using Foodplanner.Domain.ShoppingLists.Cart;

namespace Foodplanner.Domain.ShoppingLists.Sync;

/// <summary>The search result of one ingredient. <see cref="ExistingProductId"/> is the household's current choice, only given when it is re-checked.</summary>
public sealed record SyncSearched(Guid IngredientId, string Name, IReadOnlyList<ProductCandidate> Hits, string? ExistingProductId);

public enum SyncCategory
{
    /// <summary>A search hit that the household usually buys. Pre-ticked.</summary>
    UsuallyBought,

    /// <summary>The best available hit. Not ticked.</summary>
    Suggestion,

    /// <summary>The household's current choice is still among the available hits. Not ticked, no change proposed.</summary>
    Current,

    /// <summary>Nothing usable: the ingredient is left alone.</summary>
    NotFound,
}

public enum SyncNotFoundReason
{
    None,
    NoMatch,
    Unavailable,
}

/// <summary>One product of a row's picker. Exists only in the response of the review; never stored.</summary>
public sealed record SyncOption(string ProductId, string ProductName, bool IsUsual, bool IsCurrent);

/// <summary>
/// One row of the review. <see cref="SelectedProductId"/> is the usual or suggested product (null for <see cref="SyncCategory.Current"/> and
/// <see cref="SyncCategory.NotFound"/>). <see cref="HasExistingChoice"/> with a selected product means the row replaces a choice that cannot be
/// used now. Names and ids live in the response only.
/// </summary>
public sealed record SyncRow(
    Guid IngredientId,
    string IngredientName,
    SyncCategory Category,
    SyncNotFoundReason Reason,
    bool HasExistingChoice,
    string? SelectedProductId,
    bool Preticked,
    IReadOnlyList<SyncOption> Options);

/// <summary>
/// The classification of T-130, built on the product rule of <see cref="ProductMatcher"/> (T-052/T-114, not duplicated): only available hits
/// are candidates; the best-ranked usual hit is "usually bought" (pre-ticked); otherwise the first candidate is a suggestion; no usable hit
/// is "not found". A usual id that the ingredient's own search did not return is never offered. An existing choice among the candidates is
/// "current"; one that cannot be used is replaced by an unticked row. Existing choices are never pre-ticked.
/// </summary>
public static class SyncClassifier
{
    /// <summary>Rows ordered by category (usually bought, suggestion, current, not found), then by the order of <paramref name="searched"/>.</summary>
    public static IReadOnlyList<SyncRow> Classify(IReadOnlyList<SyncSearched> searched, IReadOnlySet<string> usuallyBought)
    {
        ArgumentNullException.ThrowIfNull(searched);
        ArgumentNullException.ThrowIfNull(usuallyBought);
        return
        [
            .. searched.Select((s, index) => (Row: ClassifyOne(s, usuallyBought), index))
                .OrderBy(r => r.Row.Category)
                .ThenBy(r => r.index)
                .Select(r => r.Row),
        ];
    }

    public static SyncRow ClassifyOne(SyncSearched item, IReadOnlySet<string> usuallyBought)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(usuallyBought);
        var hasExisting = item.ExistingProductId is not null;
        var match = ProductMatcher.Match(item.Hits, usuallyBought, item.ExistingProductId);
        switch (match.Outcome)
        {
            case ProductMatchOutcome.NoMatch:
                return NotFound(item, hasExisting, SyncNotFoundReason.NoMatch);
            case ProductMatchOutcome.Unavailable:
                return NotFound(item, hasExisting, SyncNotFoundReason.Unavailable);
        }

        var product = match.Product!;
        var options = ProductMatcher.Alternatives(item.Hits, product, item.ExistingProductId)
            .Select(a => new SyncOption(a.ProductId, a.ProductName, usuallyBought.Contains(a.ProductId), a.IsPreferred))
            .ToList();
        if (match.Source == ProductMatchSource.Preferred)
        {
            return new SyncRow(item.IngredientId, item.Name, SyncCategory.Current, SyncNotFoundReason.None, true, null, false, options);
        }

        var usual = match.Source == ProductMatchSource.UsuallyBought;
        return new SyncRow(
            item.IngredientId,
            item.Name,
            usual ? SyncCategory.UsuallyBought : SyncCategory.Suggestion,
            SyncNotFoundReason.None,
            hasExisting,
            product.ProductId,
            Preticked: usual && !hasExisting,
            options);
    }

    private static SyncRow NotFound(SyncSearched item, bool hasExisting, SyncNotFoundReason reason) =>
        new(item.IngredientId, item.Name, SyncCategory.NotFound, reason, hasExisting, null, false, []);
}
