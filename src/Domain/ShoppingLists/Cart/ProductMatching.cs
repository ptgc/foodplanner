namespace Foodplanner.Domain.ShoppingLists.Cart;

/// <summary>A search hit of a provider: its id (opaque to us), its name and whether it can be bought right now. Never stored.</summary>
public sealed record ProductCandidate(string ProductId, string Name, bool IsAvailable);

public enum ProductMatchOutcome
{
    /// <summary>A product was chosen.</summary>
    Matched,

    /// <summary>The search found nothing.</summary>
    NoMatch,

    /// <summary>The search found products, but none can be bought right now.</summary>
    Unavailable,
}

/// <summary>Why a product was chosen (T-114). T-052 adds the household's own pick as a source above these two.</summary>
public enum ProductMatchSource
{
    /// <summary>The first available hit.</summary>
    FirstHit,

    /// <summary>A hit that the household has bought before, according to the provider's purchase history.</summary>
    UsuallyBought,
}

public sealed record ProductMatch(ProductMatchOutcome Outcome, ProductCandidate? Product, ProductMatchSource Source = ProductMatchSource.FirstHit);

/// <summary>
/// The product rule of T-053 (Q-032 answer) and T-114: of the hits for one item, in the provider's order, only available hits are
/// candidates. The best-ranked candidate whose id is in the "usually bought" set wins; otherwise the first candidate wins. A usual
/// product that the search for this item did not return is never chosen. Ids are compared exactly. No pack-size, price or brand
/// logic: the household swaps products in the provider's own cart afterwards.
/// </summary>
public static class ProductMatcher
{
    private static readonly IReadOnlySet<string> NoUsualProducts = new HashSet<string>(StringComparer.Ordinal);

    public static ProductMatch Match(IReadOnlyList<ProductCandidate> hits) => Match(hits, NoUsualProducts);

    public static ProductMatch Match(IReadOnlyList<ProductCandidate> hits, IReadOnlySet<string> usuallyBought)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(usuallyBought);
        if (hits.Count == 0)
        {
            return new ProductMatch(ProductMatchOutcome.NoMatch, null);
        }

        ProductCandidate? first = null;
        foreach (var hit in hits)
        {
            if (!hit.IsAvailable)
            {
                continue;
            }

            if (usuallyBought.Contains(hit.ProductId))
            {
                return new ProductMatch(ProductMatchOutcome.Matched, hit, ProductMatchSource.UsuallyBought);
            }

            first ??= hit;
        }

        return first is null
            ? new ProductMatch(ProductMatchOutcome.Unavailable, null)
            : new ProductMatch(ProductMatchOutcome.Matched, first);
    }
}
