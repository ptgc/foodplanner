namespace Foodplanner.Domain.ShoppingLists.Cart;

/// <summary>A search hit of a provider: its id (opaque to us), its name and whether it can be bought right now. Never stored.</summary>
public sealed record ProductCandidate(string ProductId, string Name, bool IsAvailable);

/// <summary>One product offered in a line's picker (T-052). Exists only in the response of the fill; never stored.</summary>
public sealed record CartAlternative(string ProductId, string ProductName, bool IsAdded, bool IsPreferred);

public enum ProductMatchOutcome
{
    /// <summary>A product was chosen.</summary>
    Matched,

    /// <summary>The search found nothing.</summary>
    NoMatch,

    /// <summary>The search found products, but none can be bought right now.</summary>
    Unavailable,
}

/// <summary>Why a product was chosen: the household's own pick (T-052), its purchase history (T-114), or the first available hit.</summary>
public enum ProductMatchSource
{
    /// <summary>The first available hit.</summary>
    FirstHit,

    /// <summary>A hit that the household has bought before, according to the provider's purchase history.</summary>
    UsuallyBought,

    /// <summary>The product the household chose for this ingredient (T-052).</summary>
    Preferred,
}

/// <summary>Why the household's own pick for an item could not be used (T-052). <see cref="None"/>: it was used, or the item has no pick.</summary>
public enum PreferenceProblem
{
    None,

    /// <summary>The picked product is among the hits but cannot be bought right now (for example sold out).</summary>
    Unavailable,

    /// <summary>The picked product is not among the hits (gone from the assortment, or ranked outside the page).</summary>
    NotFound,
}

public sealed record ProductMatch(
    ProductMatchOutcome Outcome,
    ProductCandidate? Product,
    ProductMatchSource Source = ProductMatchSource.FirstHit,
    PreferenceProblem PreferenceProblem = PreferenceProblem.None);

/// <summary>
/// The product rule of T-053 (Q-032 answer), T-114 and T-052: of the hits for one item, in the provider's order, only available
/// hits are candidates. The household's own pick wins, then the best-ranked product the household usually buys, then the first
/// candidate. A pick or a usual product that the search for this item did not return is never chosen. Ids are compared exactly.
/// No pack-size, price or brand logic: the household swaps products in the provider's own cart afterwards.
/// </summary>
public static class ProductMatcher
{
    /// <summary>The most alternatives a line offers.</summary>
    public const int MaxAlternatives = 5;

    private static readonly IReadOnlySet<string> NoUsualProducts = new HashSet<string>(StringComparer.Ordinal);

    public static ProductMatch Match(IReadOnlyList<ProductCandidate> hits) => Match(hits, NoUsualProducts);

    public static ProductMatch Match(IReadOnlyList<ProductCandidate> hits, IReadOnlySet<string> usuallyBought) =>
        Match(hits, usuallyBought, null);

    /// <summary>
    /// Order of rules (T-052): the available hit whose id equals <paramref name="preferredProductId"/>; then the best-ranked usual
    /// product; then the first available hit. A pick that cannot be used does not stop the item: the result says why
    /// (<see cref="ProductMatch.PreferenceProblem"/>) when a product is chosen otherwise. Nothing available stays
    /// <see cref="ProductMatchOutcome.Unavailable"/> without a mention of the pick.
    /// </summary>
    public static ProductMatch Match(IReadOnlyList<ProductCandidate> hits, IReadOnlySet<string> usuallyBought, string? preferredProductId)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(usuallyBought);
        if (hits.Count == 0)
        {
            return new ProductMatch(ProductMatchOutcome.NoMatch, null);
        }

        ProductCandidate? first = null;
        ProductCandidate? usual = null;
        var preferredSeen = false;
        foreach (var hit in hits)
        {
            var isPreferred = preferredProductId is not null && string.Equals(hit.ProductId, preferredProductId, StringComparison.Ordinal);
            if (!hit.IsAvailable)
            {
                preferredSeen |= isPreferred;
                continue;
            }

            if (isPreferred)
            {
                return new ProductMatch(ProductMatchOutcome.Matched, hit, ProductMatchSource.Preferred);
            }

            if (usual is null && usuallyBought.Contains(hit.ProductId))
            {
                usual = hit;
            }

            first ??= hit;
        }

        if (first is null)
        {
            return new ProductMatch(ProductMatchOutcome.Unavailable, null);
        }

        var problem = preferredProductId is null
            ? PreferenceProblem.None
            : preferredSeen ? PreferenceProblem.Unavailable : PreferenceProblem.NotFound;
        return usual is not null
            ? new ProductMatch(ProductMatchOutcome.Matched, usual, ProductMatchSource.UsuallyBought, problem)
            : new ProductMatch(ProductMatchOutcome.Matched, first, ProductMatchSource.FirstHit, problem);
    }

    /// <summary>
    /// The products a line offers in its picker (T-052): the item's available hits in rank order, at most <see cref="MaxAlternatives"/>,
    /// always including the added product (it replaces the last entry when it was ranked beyond the limit).
    /// </summary>
    public static IReadOnlyList<CartAlternative> Alternatives(IReadOnlyList<ProductCandidate> hits, ProductCandidate added, string? preferredProductId)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(added);
        var chosen = hits.Where(h => h.IsAvailable).DistinctBy(h => h.ProductId, StringComparer.Ordinal).Take(MaxAlternatives).ToList();
        if (!chosen.Any(h => h.ProductId == added.ProductId))
        {
            if (chosen.Count == MaxAlternatives)
            {
                chosen.RemoveAt(chosen.Count - 1);
            }

            chosen.Add(added);
        }

        return
        [
            .. chosen.Select(h => new CartAlternative(
                h.ProductId, h.Name, h.ProductId == added.ProductId, preferredProductId is not null && h.ProductId == preferredProductId)),
        ];
    }
}
