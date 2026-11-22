using System.Globalization;
using System.Text.Json.Serialization;
using Foodplanner.Api.Authorization;
using Foodplanner.Api.Localization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Security;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.ShoppingLists.Cart;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Oda;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Features.ShoppingLists;

/// <summary>The user has seen what will be sent and confirmed. Anything but <c>true</c> is refused.</summary>
public sealed record OdaStartRequest(bool? Confirmed);

/// <summary>Where to send the browser to sign in at Oda. Contains no secret: the PKCE verifier stays in the encrypted flow cookie.</summary>
public sealed record OdaStartResponse(string AuthorizeUrl);

/// <summary>What Oda redirected the browser back with: <c>state</c> plus <c>code</c>, or <c>state</c> plus <c>error</c> (the user cancelled).</summary>
public sealed record OdaCompleteRequest(string? State, string? Code, string? Error);

/// <summary>Why a product was added: the household's own choice (T-052), what it usually buys (T-114), or the first available hit.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OdaLineSource
{
    /// <summary>The household's own choice for this ingredient.</summary>
    [JsonStringEnumMemberName("preferred")]
    Preferred,

    /// <summary>The household has bought this product before.</summary>
    [JsonStringEnumMemberName("usually-bought")]
    UsuallyBought,

    /// <summary>The first available search hit.</summary>
    [JsonStringEnumMemberName("first-hit")]
    FirstHit,
}

/// <summary>Whether the purchase history was used (T-114).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OdaHistoryStatus
{
    /// <summary>The purchase history was read.</summary>
    [JsonStringEnumMemberName("used")]
    Used,

    /// <summary>It could not be read; first hits were used.</summary>
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,

    /// <summary>Not asked for.</summary>
    [JsonStringEnumMemberName("skipped")]
    Skipped,
}

/// <summary>Why the household's choice for an ingredient was not used (T-052).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OdaPreferenceProblem
{
    /// <summary>The chosen product was among the hits but cannot be bought now (for example sold out).</summary>
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,

    /// <summary>The chosen product was not among the hits (gone from the range or ranked outside the page).</summary>
    [JsonStringEnumMemberName("not-found")]
    NotFound,
}

/// <summary>One product the picker offers for a line (T-052), from the same search as the fill. Never stored.</summary>
public sealed record OdaAlternativeResponse(long ProductId, string ProductName, bool IsAdded, bool IsPreferred);

/// <summary>
/// <c>Source</c>: see <see cref="OdaLineSource"/>. <c>IngredientId</c> is null for a manual item. <c>ProductId</c> is the added product's
/// Oda id. <c>HasPreference</c>: the household has a choice for the ingredient (used or not). <c>PreferenceProblem</c>: why it was not used.
/// <c>Alternatives</c>: up to 5 available hits of this fill, in Oda's order, the added one always among them; empty for a manual item and in
/// the body of a 502. Names and ids here exist only in this response.
/// </summary>
public sealed record OdaAddedItemResponse(
    Guid ItemId,
    string Name,
    string ProductName,
    int Quantity,
    OdaLineSource Source,
    Guid? IngredientId,
    long ProductId,
    bool HasPreference,
    OdaPreferenceProblem? PreferenceProblem,
    OdaAlternativeResponse[] Alternatives);

/// <summary><c>Status</c>: see <see cref="OdaHistoryStatus"/>. <c>Count</c>: added lines with source <c>usually-bought</c>. No product data.</summary>
public sealed record OdaUsuallyBoughtResponse(OdaHistoryStatus Status, int Count);

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
    string? ConnectionMessage,
    OdaUsuallyBoughtResponse UsuallyBought);

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
            return Results.Ok(new OdaCartResponse("cancelled", null, [], [], [], ConnectionClosed: true, ConnectionMessage: null, new OdaUsuallyBoughtResponse(OdaHistoryStatus.Skipped, 0)));
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

        // The household's own choices (T-052), read through the filtered set before Oda is called. An in-memory map for this request only.
        var preferences = (await db.PreferredOdaProducts.AsNoTracking().Select(p => new { p.IngredientId, p.OdaProductId }).ToListAsync(cancellationToken))
            .ToDictionary(p => p.IngredientId, p => p.OdaProductId.ToString(CultureInfo.InvariantCulture));

        var outcome = await action.RunAsync(householdId, userId, new OdaSecret(code), new OdaSecret(state.Verifier), lines, preferences, cancellationToken);
        if (outcome.Fill is not { } fill)
        {
            return AppProblems.Problem(
                StatusCodes.Status502BadGateway,
                AppProblems.OdaUnavailable,
                "Oda is not available",
                "The cart could not be filled. Share or copy the list as text instead.",
                new Dictionary<string, object?>
                {
                    ["added"] = outcome.AddedBeforeFailure.Select(l => ToResponse(l, withAlternatives: false)).ToArray(),
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
            [.. fill.Added.Select(l => ToResponse(l, withAlternatives: true))],
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
            ConnectionMessage(outcome.ConnectionClosed),
            new OdaUsuallyBoughtResponse(
                fill.UsuallyBought switch
                {
                    UsuallyBoughtStatus.Used => OdaHistoryStatus.Used,
                    UsuallyBoughtStatus.Unavailable => OdaHistoryStatus.Unavailable,
                    _ => OdaHistoryStatus.Skipped,
                },
                fill.UsuallyBoughtCount)));
    }

    private static OdaAddedItemResponse ToResponse(CartAddedLine line, bool withAlternatives) => new(
        line.ItemId,
        line.Name,
        line.ProductName,
        line.Quantity,
        line.Source switch
        {
            ProductMatchSource.Preferred => OdaLineSource.Preferred,
            ProductMatchSource.UsuallyBought => OdaLineSource.UsuallyBought,
            _ => OdaLineSource.FirstHit,
        },
        line.IngredientId,
        ParseId(line.ProductId),
        line.HasPreference,
        line.PreferenceProblem switch
        {
            Domain.ShoppingLists.Cart.PreferenceProblem.Unavailable => OdaPreferenceProblem.Unavailable,
            Domain.ShoppingLists.Cart.PreferenceProblem.NotFound => OdaPreferenceProblem.NotFound,
            _ => null,
        },
        withAlternatives
            ? [.. line.AlternativeList.Select(a => new OdaAlternativeResponse(ParseId(a.ProductId), a.ProductName, a.IsAdded, a.IsPreferred))]
            : []);

    /// <summary>Oda's ids are positive integers (the client only accepts those); anything else would be a bug, shown as 0 rather than failing the fill.</summary>
    private static long ParseId(string id) => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string? ConnectionMessage(bool closed) =>
        closed ? null : "The connection to Oda could not be closed. Remove Foodplanner under Privacy & security in your Oda account.";

    private static IResult NotConfigured() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.OdaNotConfigured, "Oda is not set up", "Sending to Oda is not available here. Share or copy the list as text.");

    /// <summary>The list is always read here, server-side, in the household of the route; the client never sends items.</summary>
    private static async Task<IReadOnlyList<CartLine>> LinesAsync(
        AppDbContext db, DateOnly monday, string locale, Guid userId, CancellationToken cancellationToken)
    {
        var list = await ShoppingListEndpoints.ReadAsync(db, monday, locale, userId, cancellationToken);
        return CartLineSelector.Select(list.Main.Select(i => new CartSourceItem(i.Id, i.Name, i.Checked, i.IsManual ? null : i.IngredientId)));
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
