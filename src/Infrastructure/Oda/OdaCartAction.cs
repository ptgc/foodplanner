using System.Diagnostics;
using Foodplanner.Domain.ShoppingLists.Cart;
using Microsoft.Extensions.Logging;

namespace Foodplanner.Infrastructure.Oda;

/// <summary>
/// The result of one cart action. <see cref="Fill"/> is set when the fill finished; otherwise <see cref="AddedBeforeFailure"/>
/// is what reached the cart before the failure. <see cref="ConnectionClosed"/>: the token was revoked at Oda.
/// </summary>
public sealed record OdaActionOutcome(CartFillResult? Fill, IReadOnlyList<CartAddedLine> AddedBeforeFailure, bool ConnectionClosed)
{
    public bool Succeeded => Fill is not null;
}

/// <summary>
/// The whole session-only action of T-053, in one request on one replica: discover, exchange the code (public client, PKCE), open
/// an MCP session, fill the cart, and in a finally path revoke the token (success, partial result, any error, cancellation).
/// The tokens never leave this method. Nothing is cached or kept: an instance holds no state between actions.
/// </summary>
public sealed class OdaCartAction(OdaOAuth oauth, OdaMcpSessionFactory sessions, ILogger<OdaCartAction> logger)
{
    /// <summary>Throws only for cancellation. Every Oda failure is returned as an outcome without <see cref="OdaActionOutcome.Fill"/>.</summary>
    public async Task<OdaActionOutcome> RunAsync(
        Guid householdId, Guid userId, OdaSecret code, OdaSecret verifier, IReadOnlyList<CartLine> lines, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        OdaEndpoints? endpoints = null;
        OdaTokens? tokens = null;
        CartFillResult? fill = null;
        IReadOnlyList<CartAddedLine> partial = [];
        var closed = true; // no token (denied, refused code, discovery failure) means no connection to close
        try
        {
            endpoints = await oauth.DiscoverAsync(cancellationToken);
            tokens = await oauth.ExchangeAsync(endpoints, code, verifier, cancellationToken);
            var session = await sessions.OpenAsync(tokens.Access, cancellationToken);
            fill = await CartFiller.FillAsync(session, lines, cancellationToken);
        }
        catch (CartFillInterruptedException ex)
        {
            partial = ex.Added;
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

        var added = fill?.Added.Count ?? partial.Count;
        logger.LogInformation(
            "Oda cart action for household {HouseholdId} by user {UserId}: {Outcome}, added {Added}, not added {NotAdded}, connection closed {Closed}, usually bought {UsuallyBought} (history {History}), {Milliseconds} ms",
            householdId,
            userId,
            fill is null ? "failed" : "finished",
            added,
            fill?.NotAdded.Count ?? 0,
            closed,
            fill?.UsuallyBoughtCount ?? 0,
            fill?.UsuallyBought.ToString() ?? "none",
            clock.ElapsedMilliseconds);
        return new OdaActionOutcome(fill, partial, closed);
    }
}
