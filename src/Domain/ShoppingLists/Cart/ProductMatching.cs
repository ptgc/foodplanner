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

public sealed record ProductMatch(ProductMatchOutcome Outcome, ProductCandidate? Product);

/// <summary>
/// The product rule of T-053 (Q-032 answer): of the hits for one item, in the provider's order, the first available one wins.
/// No pack-size, price or brand logic: the household swaps products in the provider's own cart afterwards.
/// </summary>
public static class ProductMatcher
{
    public static ProductMatch Match(IReadOnlyList<ProductCandidate> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count == 0)
        {
            return new ProductMatch(ProductMatchOutcome.NoMatch, null);
        }

        var first = hits.FirstOrDefault(h => h.IsAvailable);
        return first is null
            ? new ProductMatch(ProductMatchOutcome.Unavailable, null)
            : new ProductMatch(ProductMatchOutcome.Matched, first);
    }
}
