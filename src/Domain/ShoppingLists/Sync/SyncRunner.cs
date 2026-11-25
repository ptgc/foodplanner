using Foodplanner.Domain.ShoppingLists.Cart;

namespace Foodplanner.Domain.ShoppingLists.Sync;

/// <summary>Why a run ended before every selected ingredient was searched.</summary>
public enum SyncStopped
{
    /// <summary>The time budget was spent.</summary>
    Time,

    /// <summary>The provider answered with its usage limit (429). Never retried or worked around.</summary>
    ProviderLimit,

    /// <summary>The provider failed or answered in a shape we do not know, after at least one batch succeeded.</summary>
    ProviderError,
}

/// <summary><see cref="SearchedCount"/> is how many of the given ingredients were searched; the rest were not checked.</summary>
public sealed record SyncRunResult(IReadOnlyList<SyncRow> Rows, SyncStopped? Stopped, UsuallyBoughtStatus UsuallyBought, int SearchedCount);

/// <summary>The provider's usage limit (HTTP 429). A run that has results stops with them; it never retries.</summary>
public sealed class ProviderLimitException(string message) : ProviderUnavailableException(message);

/// <summary>
/// The provider calls of "Sync with Oda" (T-130): <c>ceil(n / 10)</c> searches, strictly one after the other and each sent once, then at most
/// one read of the purchase history, and never a cart call. A time budget is checked before every call. A stop after at least one successful
/// batch returns what was found; a failure before that throws <see cref="ProviderUnavailableException"/>.
/// </summary>
public static class SyncRunner
{
    public const int BatchSize = 10;

    /// <param name="existing">The household's current choices (ingredient id to product id) for the ingredients that are re-checked; empty otherwise.</param>
    public static async Task<SyncRunResult> RunAsync(
        ICartProviderSession session,
        IReadOnlyList<SyncIngredient> ingredients,
        IReadOnlyDictionary<Guid, string> existing,
        TimeSpan budget,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(ingredients);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(time);
        var started = time.GetTimestamp();
        var searched = new List<SyncSearched>();
        SyncStopped? stopped = null;
        foreach (var batch in ingredients.Chunk(BatchSize))
        {
            if (time.GetElapsedTime(started) >= budget)
            {
                stopped = SyncStopped.Time;
                break;
            }

            try
            {
                var hits = await session.SearchAsync([.. batch.Select(i => i.Name)], cancellationToken);
                if (hits.Count != batch.Length)
                {
                    throw new ProviderUnavailableException("The search did not answer every query.");
                }

                for (var i = 0; i < batch.Length; i++)
                {
                    searched.Add(new SyncSearched(batch[i].IngredientId, batch[i].Name, hits[i], existing.GetValueOrDefault(batch[i].IngredientId)));
                }
            }
            catch (ProviderUnavailableException ex) when (searched.Count > 0)
            {
                stopped = ex is ProviderLimitException ? SyncStopped.ProviderLimit : SyncStopped.ProviderError;
                break;
            }
        }

        if (searched.Count == 0 && ingredients.Count > 0)
        {
            throw new ProviderUnavailableException("Nothing could be searched before the run stopped.");
        }

        // One history call, after the last search, only when something could be offered. A stopped run does not call the provider again.
        var history = UsuallyBoughtStatus.Skipped;
        IReadOnlySet<string> usual = new HashSet<string>(StringComparer.Ordinal);
        if (stopped is null && searched.Any(s => s.Hits.Any(h => h.IsAvailable)))
        {
            if (time.GetElapsedTime(started) >= budget)
            {
                return new SyncRunResult(SyncClassifier.Classify(searched, usual), null, UsuallyBoughtStatus.Unavailable, searched.Count);
            }

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

        return new SyncRunResult(SyncClassifier.Classify(searched, usual), stopped, history, searched.Count);
    }
}
