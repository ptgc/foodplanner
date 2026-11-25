using System.Diagnostics;
using Foodplanner.Domain.ShoppingLists.Cart;
using Foodplanner.Domain.ShoppingLists.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Foodplanner.Infrastructure.Oda;

/// <summary>
/// The result of one sync action (T-130). <see cref="Result"/> is null when the run failed before any batch succeeded. <see cref="ConnectionClosed"/>:
/// the token was revoked at Oda. Holds one reference to the request's result, no collection of its own.
/// </summary>
public sealed record OdaSyncOutcome(SyncRunResult? Result, bool ConnectionClosed)
{
    public bool Succeeded => Result is not null;
}

/// <summary>
/// The session-only action of "Sync with Oda" (T-130), in one request on one replica, beside <see cref="OdaCartAction"/> and sharing its
/// OAuth code and session: discover, exchange the code (public client, PKCE), open an MCP session that needs only <c>product_search</c>,
/// run the searches and the one history read (<see cref="SyncRunner"/>), and in a finally path revoke the token (success, partial result,
/// any error, cancellation). Tokens, hits and ids live in this method only; nothing is cached or kept.
/// </summary>
public sealed class OdaSyncAction(
    OdaOAuth oauth, OdaMcpSessionFactory sessions, IOptions<OdaOptions> options, TimeProvider time, ILogger<OdaSyncAction> logger)
{
    /// <summary>Throws only for cancellation. Every Oda failure is returned as an outcome without <see cref="OdaSyncOutcome.Result"/>.</summary>
    public async Task<OdaSyncOutcome> RunAsync(
        Guid householdId,
        Guid userId,
        OdaSecret code,
        OdaSecret verifier,
        IReadOnlyList<SyncIngredient> ingredients,
        IReadOnlyDictionary<Guid, string> existing,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        OdaEndpoints? endpoints = null;
        OdaTokens? tokens = null;
        SyncRunResult? result = null;
        var closed = true; // no token (refused code, discovery failure) means no connection to close
        try
        {
            endpoints = await oauth.DiscoverAsync(cancellationToken);
            tokens = await oauth.ExchangeAsync(endpoints, code, verifier, cancellationToken);
            var session = await sessions.OpenAsync(tokens.Access, cancellationToken, OdaSessionPurpose.Sync);
            var budget = TimeSpan.FromSeconds(Math.Max(1, options.Value.SyncBudgetSeconds));
            result = await SyncRunner.RunAsync(session, ingredients, existing, budget, time, cancellationToken);
        }
        catch (ProviderUnavailableException)
        {
            // Nothing from Oda is logged beyond our own fixed words.
        }
        finally
        {
            if (tokens is not null && endpoints is not null)
            {
                closed = await oauth.RevokeAsync(endpoints, tokens);
            }
        }

        logger.LogInformation(
            "Oda sync for household {HouseholdId} by user {UserId}: {Outcome}, searched {Searched}, usually bought {UsuallyBought}, suggestions {Suggestions}, not found {NotFound}, stopped {Stopped}, history {History}, connection closed {Closed}, {Milliseconds} ms",
            householdId,
            userId,
            result is null ? "failed" : "finished",
            result?.SearchedCount ?? 0,
            result?.Rows.Count(r => r.Category == SyncCategory.UsuallyBought) ?? 0,
            result?.Rows.Count(r => r.Category == SyncCategory.Suggestion) ?? 0,
            result?.Rows.Count(r => r.Category == SyncCategory.NotFound) ?? 0,
            result?.Stopped?.ToString() ?? "none",
            result?.UsuallyBought.ToString() ?? "none",
            closed,
            clock.ElapsedMilliseconds);
        return new OdaSyncOutcome(result, closed);
    }
}
