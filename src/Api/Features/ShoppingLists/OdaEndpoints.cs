using Foodplanner.Api.Authorization;
using Foodplanner.Api.Localization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Security;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.ShoppingLists.Cart;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Oda;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Features.ShoppingLists;

/// <summary>The user has seen what will be sent and confirmed. Anything but <c>true</c> is refused.</summary>
public sealed record OdaStartRequest(bool? Confirmed);

/// <summary>Where to send the browser to sign in at Oda. Contains no secret: the PKCE verifier stays in the encrypted flow cookie.</summary>
public sealed record OdaStartResponse(string AuthorizeUrl);

/// <summary>What Oda redirected the browser back with: <c>state</c> plus <c>code</c>, or <c>state</c> plus <c>error</c> (the user cancelled).</summary>
public sealed record OdaCompleteRequest(string? State, string? Code, string? Error);

public sealed record OdaAddedItemResponse(Guid ItemId, string Name, string ProductName, int Quantity);

/// <summary><c>Reason</c>: <c>no-match</c>, <c>unavailable</c> or <c>rejected</c>.</summary>
public sealed record OdaNotAddedItemResponse(Guid ItemId, string Name, string Reason);

/// <summary>
/// <c>Status</c>: <c>done</c>, <c>partial</c>, <c>nothing-added</c> or <c>cancelled</c>. <c>CartUrl</c> is Oda's link to the cart (null when
/// it could not be read). <c>ConnectionClosed</c>: the sign-in was revoked at Oda; when false, <c>ConnectionMessage</c> tells the user to
/// remove the connection under Privacy &amp; security in their Oda account. Nothing in here is stored.
/// </summary>
public sealed record OdaCartResponse(
    string Status,
    string? CartUrl,
    OdaAddedItemResponse[] Added,
    OdaNotAddedItemResponse[] NotAdded,
    string[] CartMessages,
    bool ConnectionClosed,
    string? ConnectionMessage);

/// <summary>
/// Send the shopping list to the household's Oda cart, session-only (T-053, ADR 0008, Q-024/Q-025/Q-033). Owner or Adult, never kid
/// mode: both endpoints carry <see cref="Policies.HouseholdAdult"/> and <see cref="Policies.FullSession"/>. <c>start</c> makes no call to Oda;
/// <c>cart</c> does the whole action in one request (exchange the code, search, fill, revoke). Nothing from Oda is stored.
/// </summary>
public static class OdaEndpoints
{
    public static IEndpointRouteBuilder MapOdaEndpoints(this IEndpointRouteBuilder api)
    {
        var oda = api.MapHouseholdScopedGroup().MapGroup("/shopping-lists/{weekStart}/oda");
        oda.RequireAuthorization(Policies.HouseholdAdult);
        oda.RequireAuthorization(Policies.FullSession);
        oda.RequireRateLimiting(RateLimitPolicies.OdaAction);

        oda.MapPost("/start", Start)
            .WithName("StartOdaCart")
            .Produces<OdaStartResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        oda.MapPost("/cart", Cart)
            .WithName("CompleteOdaCart")
            .Produces<OdaCartResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        return api;
    }

    private static async Task<IResult> Start(
        Guid householdId, string weekStart, OdaStartRequest? request, HttpContext http, RequestLocale locale, ICurrentUser user, TimeProvider time,
        AppDbContext db, OdaOAuth oauth, OdaFlowCookie flow, IOptions<OdaOptions> options, CancellationToken cancellationToken)
    {
        if (!ShoppingListEndpoints.IsWeekStart(weekStart, out var monday))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["weekStart"] = ["The week must be the Monday of an ISO week, as yyyy-MM-dd."] });
        }

        if (!options.Value.IsConfigured || user.UserId is not { } userId)
        {
            return NotConfigured();
        }

        if (request?.Confirmed is not true)
        {
            return AppProblems.Problem(
                StatusCodes.Status400BadRequest, AppProblems.OdaConfirmationRequired, "Confirmation required", "The user must confirm what is sent to Oda.");
        }

        var lines = await LinesAsync(db, monday, locale.Code, userId, cancellationToken);
        if (CheckLines(lines, options.Value) is { } refused)
        {
            return refused;
        }

        var state = OdaOAuth.NewState();
        var verifier = OdaOAuth.NewVerifier();
        if (oauth.BuildAuthorizeUrl(state, verifier) is not { } authorize)
        {
            return NotConfigured();
        }

        flow.Issue(http.Response, new OdaFlowState(verifier, state, householdId, monday, userId, time.GetUtcNow().Add(OdaFlowCookie.Lifetime)));
        return Results.Ok(new OdaStartResponse(authorize.AbsoluteUri));
    }

    private static async Task<IResult> Cart(
        Guid householdId, string weekStart, OdaCompleteRequest? request, HttpContext http, RequestLocale locale, ICurrentUser user, AppDbContext db,
        OdaFlowCookie flow, OdaCartAction action, IOptions<OdaOptions> options, CancellationToken cancellationToken)
    {
        if (!ShoppingListEndpoints.IsWeekStart(weekStart, out var monday))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["weekStart"] = ["The week must be the Monday of an ISO week, as yyyy-MM-dd."] });
        }

        if (!options.Value.IsConfigured || user.UserId is not { } userId)
        {
            return NotConfigured();
        }

        // Consumes the flow state first: it is single use whatever happens next, and no call to Oda is made without it.
        if (flow.Take(http, request?.State, householdId, monday, userId) is not { } state)
        {
            return AppProblems.Problem(
                StatusCodes.Status400BadRequest, AppProblems.OdaStateInvalid, "Sign-in expired", "Start sending to Oda again.");
        }

        if (!string.IsNullOrWhiteSpace(request?.Error))
        {
            return Results.Ok(new OdaCartResponse("cancelled", null, [], [], [], ConnectionClosed: true, ConnectionMessage: null));
        }

        var code = request?.Code;
        if (string.IsNullOrWhiteSpace(code))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["code is required."] });
        }

        var lines = await LinesAsync(db, monday, locale.Code, userId, cancellationToken);
        if (CheckLines(lines, options.Value) is { } refused)
        {
            return refused;
        }

        var outcome = await action.RunAsync(householdId, userId, new OdaSecret(code), new OdaSecret(state.Verifier), lines, cancellationToken);
        if (outcome.Fill is not { } fill)
        {
            return AppProblems.Problem(
                StatusCodes.Status502BadGateway,
                AppProblems.OdaUnavailable,
                "Oda is not available",
                "The cart could not be filled. Share or copy the list as text instead.",
                new Dictionary<string, object?>
                {
                    ["added"] = outcome.AddedBeforeFailure.Select(ToResponse).ToArray(),
                    ["connectionClosed"] = outcome.ConnectionClosed,
                    ["connectionMessage"] = ConnectionMessage(outcome.ConnectionClosed),
                    ["fallback"] = new { export = "text" },
                });
        }

        return Results.Ok(new OdaCartResponse(
            fill.Status switch
            {
                CartFillStatus.Done => "done",
                CartFillStatus.Partial => "partial",
                _ => "nothing-added",
            },
            fill.Cart?.Url,
            [.. fill.Added.Select(ToResponse)],
            [.. fill.NotAdded.Select(n => new OdaNotAddedItemResponse(
                n.ItemId,
                n.Name,
                n.Reason switch
                {
                    CartNotAddedReason.NoMatch => "no-match",
                    CartNotAddedReason.Unavailable => "unavailable",
                    _ => "rejected",
                }))],
            [.. fill.Cart?.Messages ?? []],
            outcome.ConnectionClosed,
            ConnectionMessage(outcome.ConnectionClosed)));
    }

    private static OdaAddedItemResponse ToResponse(CartAddedLine line) => new(line.ItemId, line.Name, line.ProductName, line.Quantity);

    private static string? ConnectionMessage(bool closed) =>
        closed ? null : "The connection to Oda could not be closed. Remove Foodplanner under Privacy & security in your Oda account.";

    private static IResult NotConfigured() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.OdaNotConfigured, "Oda is not set up", "Sending to Oda is not available here. Share or copy the list as text.");

    /// <summary>The list is always read here, server-side, in the household of the route; the client never sends items.</summary>
    private static async Task<IReadOnlyList<CartLine>> LinesAsync(
        AppDbContext db, DateOnly monday, string locale, Guid userId, CancellationToken cancellationToken)
    {
        var list = await ShoppingListEndpoints.ReadAsync(db, monday, locale, userId, cancellationToken);
        return CartLineSelector.Select(list.Main.Select(i => new CartSourceItem(i.Id, i.Name, i.Checked)));
    }

    private static IResult? CheckLines(IReadOnlyList<CartLine> lines, OdaOptions options) =>
        lines.Count == 0
            ? AppProblems.Problem(StatusCodes.Status409Conflict, AppProblems.OdaNothingToAdd, "Nothing to add", "The list has no unchecked items in To buy.")
            : lines.Count > options.MaxItemsPerAction
                ? AppProblems.Problem(
                    StatusCodes.Status409Conflict,
                    AppProblems.OdaTooManyItems,
                    "Too many items",
                    $"At most {options.MaxItemsPerAction} items can be sent at once.")
                : null;
}
