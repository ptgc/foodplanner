namespace Foodplanner.Domain.ShoppingLists.Cart;

/// <summary>
/// A signed-in connection to a provider cart for one action (T-053). Implementations live outside Domain, hold their credentials
/// only in memory and fail with <see cref="ProviderUnavailableException"/> when the provider is unreachable, refuses, or answers
/// in a shape they do not know. Nothing a session sees is kept after the action.
/// </summary>
public interface ICartProviderSession
{
    /// <summary>One list of hits per query, in the order of <paramref name="queries"/> and of the provider's ranking.</summary>
    Task<IReadOnlyList<IReadOnlyList<ProductCandidate>>> SearchAsync(IReadOnlyList<string> queries, CancellationToken cancellationToken);

    /// <summary>
    /// Adds one pack of each product (a delta of +1: calling this twice adds twice, so a call with an unknown outcome must never be
    /// repeated). Returns the ids of the products that are in the cart afterwards.
    /// </summary>
    Task<IReadOnlySet<string>> AddOnePackEachAsync(IReadOnlyList<string> productIds, CancellationToken cancellationToken);

    Task<CartSnapshot> ReadCartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The ids of the products the household usually buys (T-114), read once per fill and only used to choose among an item's own
    /// search hits. Optional: fails with <see cref="ProviderUnavailableException"/> when it cannot be read, and the fill goes on without it.
    /// Names and everything else the provider returns are dropped.
    /// </summary>
    Task<IReadOnlySet<string>> ReadUsuallyBoughtIdsAsync(CancellationToken cancellationToken);
}

/// <summary>The cart as the provider reports it: a link to open it and the provider's own validation messages (for example a sold-out line).</summary>
public sealed record CartSnapshot(string? Url, IReadOnlyList<string> Messages);

/// <summary>The provider cannot be used right now. The message is ours and never carries a provider body, token or code.</summary>
public class ProviderUnavailableException(string message) : Exception(message);

/// <summary>The fill stopped part-way. <see cref="Added"/> is what was put in the cart before the failure, so the user does not add it twice.</summary>
public sealed class CartFillInterruptedException(string message, IReadOnlyList<CartAddedLine> added) : ProviderUnavailableException(message)
{
    public IReadOnlyList<CartAddedLine> Added { get; } = added;
}

public enum CartNotAddedReason
{
    NoMatch,
    Unavailable,

    /// <summary>The provider's cart did not take the line.</summary>
    Rejected,
}

/// <summary>
/// A line that reached the cart. T-052: <see cref="IngredientId"/> is the item's ingredient (null for a manual item),
/// <see cref="ProductId"/> the added product, <see cref="HasPreference"/> whether the household has a pick for the ingredient (used or
/// not), <see cref="PreferenceProblem"/> why the pick was not used, <see cref="Alternatives"/> what the picker offers. Response only.
/// </summary>
public sealed record CartAddedLine(
    Guid ItemId,
    string Name,
    string ProductName,
    int Quantity,
    ProductMatchSource Source = ProductMatchSource.FirstHit,
    Guid? IngredientId = null,
    string ProductId = "",
    bool HasPreference = false,
    PreferenceProblem PreferenceProblem = PreferenceProblem.None,
    IReadOnlyList<CartAlternative>? Alternatives = null)
{
    public IReadOnlyList<CartAlternative> AlternativeList => Alternatives ?? [];
}

/// <summary>Whether the purchase history was used for this fill: read (<see cref="Used"/>), not readable (<see cref="Unavailable"/>) or not asked for (<see cref="Skipped"/>).</summary>
public enum UsuallyBoughtStatus
{
    Used,
    Unavailable,
    Skipped,
}

public sealed record CartNotAddedLine(Guid ItemId, string Name, CartNotAddedReason Reason);

public enum CartFillStatus
{
    Done,
    Partial,
    NothingAdded,
}

public sealed record CartFillResult(
    IReadOnlyList<CartAddedLine> Added, IReadOnlyList<CartNotAddedLine> NotAdded, CartSnapshot? Cart,
    UsuallyBoughtStatus UsuallyBought = UsuallyBoughtStatus.Skipped)
{
    /// <summary>Number of added lines chosen because the household usually buys the product.</summary>
    public int UsuallyBoughtCount => Added.Count(a => a.Source == ProductMatchSource.UsuallyBought);

    /// <summary>Number of added lines chosen because of the household's own pick (T-052).</summary>
    public int PreferredCount => Added.Count(a => a.Source == ProductMatchSource.Preferred);

    public CartFillStatus Status =>
        Added.Count == 0 ? CartFillStatus.NothingAdded : NotAdded.Count == 0 ? CartFillStatus.Done : CartFillStatus.Partial;
}

/// <summary>
/// Fills a provider cart from a list of lines: search (batched), read the purchase history once (T-114), pick per line the usually bought available hit or else the first available hit, add one pack each (batched),
/// read the cart once. Nothing is retried, because quantities are deltas. Never orders or pays.
/// </summary>
public static class CartFiller
{
    public const int DefaultBatchSize = 10;

    /// <param name="preferences">The household's own picks (T-052): ingredient id to the provider's product id. Null means none.</param>
    public static async Task<CartFillResult> FillAsync(
        ICartProviderSession session, IReadOnlyList<CartLine> lines, CancellationToken cancellationToken, int batchSize = DefaultBatchSize,
        IReadOnlyDictionary<Guid, string>? preferences = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        string? PreferredFor(CartLine line) =>
            line.IngredientId is { } ingredient && preferences is not null && preferences.TryGetValue(ingredient, out var id) ? id : null;

        var notAdded = new List<CartNotAddedLine>();
        var matched = new List<(CartLine Line, IReadOnlyList<ProductCandidate> Hits, ProductMatch Match, string? Preferred)>();
        var searched = new List<(CartLine Line, IReadOnlyList<ProductCandidate> Hits)>();

        // Search everything first: a failure here has touched no cart.
        foreach (var batch in lines.Chunk(batchSize))
        {
            var hits = await session.SearchAsync([.. batch.Select(l => l.Name)], cancellationToken);
            if (hits.Count != batch.Length)
            {
                throw new ProviderUnavailableException("The search did not answer every query.");
            }

            for (var i = 0; i < batch.Length; i++)
            {
                searched.Add((batch[i], hits[i]));
            }
        }

        // One history call (T-114), only when something could be matched, after the last search and before the first cart call.
        // Any failure is not a failure of the fill: no retry, the first available hits are used.
        var history = UsuallyBoughtStatus.Skipped;
        IReadOnlySet<string> usual = new HashSet<string>(StringComparer.Ordinal);
        // Skipped too when the household's own picks decide every item that has an available hit (T-052).
        if (searched.Any(s => s.Hits.Any(h => h.IsAvailable)
                              && !(PreferredFor(s.Line) is { } pick && s.Hits.Any(h => h.IsAvailable && h.ProductId == pick))))
        {
            try
            {
                usual = await session.ReadUsuallyBoughtIdsAsync(cancellationToken);
                history = UsuallyBoughtStatus.Used;
            }
            catch (ProviderUnavailableException)
            {
                history = UsuallyBoughtStatus.Unavailable;
            }
        }

        foreach (var (line, hits) in searched)
        {
            var preferred = PreferredFor(line);
            var match = ProductMatcher.Match(hits, usual, preferred);
            switch (match.Outcome)
            {
                case ProductMatchOutcome.Matched:
                    matched.Add((line, hits, match, preferred));
                    break;
                case ProductMatchOutcome.NoMatch:
                    notAdded.Add(new CartNotAddedLine(line.ItemId, line.Name, CartNotAddedReason.NoMatch));
                    break;
                default:
                    notAdded.Add(new CartNotAddedLine(line.ItemId, line.Name, CartNotAddedReason.Unavailable));
                    break;
            }
        }

        var added = new List<CartAddedLine>();
        foreach (var batch in matched.Chunk(batchSize))
        {
            IReadOnlySet<string> inCart;
            try
            {
                inCart = await session.AddOnePackEachAsync([.. batch.Select(m => m.Match.Product!.ProductId)], cancellationToken);
            }
            catch (ProviderUnavailableException ex)
            {
                // The outcome of this call is unknown and it is not repeated; only earlier batches are reported as added.
                throw new CartFillInterruptedException(ex.Message, [.. added]);
            }

            foreach (var (line, hits, match, preferred) in batch)
            {
                var product = match.Product!;
                if (inCart.Contains(product.ProductId))
                {
                    added.Add(new CartAddedLine(
                        line.ItemId,
                        line.Name,
                        product.Name,
                        1,
                        match.Source,
                        line.IngredientId,
                        product.ProductId,
                        preferred is not null,
                        match.PreferenceProblem,
                        line.IngredientId is null ? [] : ProductMatcher.Alternatives(hits, product, preferred)));
                }
                else
                {
                    notAdded.Add(new CartNotAddedLine(line.ItemId, line.Name, CartNotAddedReason.Rejected));
                }
            }
        }

        CartSnapshot? cart = null;
        try
        {
            cart = await session.ReadCartAsync(cancellationToken);
        }
        catch (ProviderUnavailableException)
        {
            // The items are in the cart already; a missing link is reported as such, not as a failure.
        }

        return new CartFillResult(added, notAdded, cart, history);
    }
}
