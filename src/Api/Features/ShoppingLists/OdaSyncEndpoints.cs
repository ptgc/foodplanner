using System.Globalization;
using System.Text.Json.Serialization;
using Foodplanner.Api.Authorization;
using Foodplanner.Api.Localization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Security;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.ShoppingLists.Cart;
using Foodplanner.Domain.ShoppingLists.Sync;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Oda;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Features.ShoppingLists;

/// <summary>
/// The user has seen what will be searched and confirmed (<c>Confirmed</c> must be true). <c>IncludeStaples</c> and <c>IncludeExisting</c> default to
/// false; <c>Offset</c> (default 0) continues a run that stopped ("Check the next ones").
/// </summary>
public sealed record OdaSyncStartRequest(bool? Confirmed, bool? IncludeStaples, bool? IncludeExisting, int? Offset);

/// <summary>
/// Where to send the browser to sign in at Oda, and how many ingredients this run will search (a count only) and how many matching
/// ingredients are left after it. No secret: the PKCE verifier stays in the encrypted flow cookie.
/// </summary>
public sealed record OdaSyncStartResponse(string AuthorizeUrl, int IngredientCount, int NotCheckedCount);

/// <summary>Why a sync run stopped early.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OdaSyncStopped
{
    /// <summary>The time budget was spent.</summary>
    [JsonStringEnumMemberName("time")]
    Time,

    /// <summary>Oda answered with its usage limit.</summary>
    [JsonStringEnumMemberName("oda-limit")]
    OdaLimit,

    /// <summary>Oda failed or answered in an unknown shape after at least one batch.</summary>
    [JsonStringEnumMemberName("oda-error")]
    OdaError,
}

/// <summary>The kind of a review row.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OdaSyncCategory
{
    /// <summary>A search hit that the household usually buys. Pre-ticked.</summary>
    [JsonStringEnumMemberName("usually-bought")]
    UsuallyBought,

    /// <summary>The best available hit. Not ticked.</summary>
    [JsonStringEnumMemberName("suggestion")]
    Suggestion,

    /// <summary>The household's current choice is still available. Nothing proposed.</summary>
    [JsonStringEnumMemberName("current")]
    Current,

    /// <summary>Nothing usable was found.</summary>
    [JsonStringEnumMemberName("not-found")]
    NotFound,
}

/// <summary>Why nothing was found.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OdaSyncReason
{
    [JsonStringEnumMemberName("no-match")]
    NoMatch,

    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
}

/// <summary>One product of a row's picker. Names and ids exist only in this response.</summary>
public sealed record OdaSyncOptionResponse(long ProductId, string ProductName, bool IsUsual, bool IsCurrent);

/// <summary>
/// One ingredient of the review. <c>SelectedProductId</c> is the usual or suggested product (null for <c>current</c> and <c>not-found</c>).
/// <c>HasExistingChoice</c>: the household has a choice (with a selected product it cannot be used now and would be replaced). <c>Reason</c> only
/// for <c>not-found</c>. <c>Preticked</c> only for <c>usually-bought</c> rows without an existing choice.
/// </summary>
public sealed record OdaSyncRowResponse(
    Guid IngredientId,
    string IngredientName,
    OdaSyncCategory Category,
    OdaSyncReason? Reason,
    bool HasExistingChoice,
    long? SelectedProductId,
    bool Preticked,
    OdaSyncOptionResponse[] Options);

/// <summary>
/// <c>Status</c>: <c>done</c>, <c>partial</c> or <c>cancelled</c>. <c>Stopped</c> says why a partial run ended; <c>NotCheckedCount</c> is how many
/// ingredients were not searched (also those above the cap of one run). <c>ConnectionClosed</c>: the sign-in was revoked at Oda; when false,
/// <c>ConnectionMessage</c> tells the user to remove the connection under Privacy &amp; security in their Oda account. Nothing in here is stored.
/// </summary>
public sealed record OdaSyncReviewResponse(
    string Status,
    OdaSyncStopped? Stopped,
    OdaHistoryStatus UsuallyBought,
    OdaSyncRowResponse[] Rows,
    int NotCheckedCount,
    bool ConnectionClosed,
    string? ConnectionMessage);

/// <summary>
/// "Sync with Oda" (T-130, ADR 0008, Q-024/Q-025/Q-039/Q-040): one session-only Oda connection searches the ingredients of the household's
/// recipes and returns a review list; the user's confirmation is saved with the bulk <c>PUT oda-preferences</c>. Owner or Adult, never kid
/// mode (policies <see cref="Policies.HouseholdAdult"/> and <see cref="Policies.FullSession"/>). <c>start</c> makes no call to Oda; <c>review</c>
/// does the whole action in one request (exchange the code, search, read the usual products, revoke). Nothing from Oda is stored or logged.
/// </summary>
public static class OdaSyncEndpoints
{
    public static IEndpointRouteBuilder MapOdaSyncEndpoints(this IEndpointRouteBuilder api)
    {
        var sync = api.MapHouseholdScopedGroup().MapGroup("/oda-sync");
        sync.RequireAuthorization(Policies.HouseholdAdult);
        sync.RequireAuthorization(Policies.FullSession);
        sync.RequireRateLimiting(RateLimitPolicies.OdaAction);

        sync.MapPost("/start", Start)
            .WithName("StartOdaSync")
            .Produces<OdaSyncStartResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        sync.MapPost("/review", Review)
            .WithName("ReviewOdaSync")
            .Produces<OdaSyncReviewResponse>()
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
        Guid householdId, OdaSyncStartRequest? request, HttpContext http, RequestLocale locale, ICurrentUser user, TimeProvider time,
        AppDbContext db, OdaOAuth oauth, OdaFlowCookie flow, IOptions<OdaOptions> options, CancellationToken cancellationToken)
    {
        if (!options.Value.IsConfigured || user.UserId is not { } userId)
        {
            return OdaEndpoints.NotConfigured();
        }

        if (request?.Confirmed is not true)
        {
            return AppProblems.Problem(
                StatusCodes.Status400BadRequest, AppProblems.OdaConfirmationRequired, "Confirmation required", "The user must confirm what is searched at Oda.");
        }

        if (request.Offset is < 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["offset"] = ["offset must be 0 or more."] });
        }

        var includeStaples = request.IncludeStaples == true;
        var includeExisting = request.IncludeExisting == true;
        var offset = request.Offset ?? 0;
        var (selection, _) = await SelectAsync(db, locale.Code, includeStaples, includeExisting, offset, options.Value, cancellationToken);
        if (selection.Selected.Count == 0)
        {
            return NothingToSync();
        }

        var state = OdaOAuth.NewState();
        var verifier = OdaOAuth.NewVerifier();
        if (oauth.BuildAuthorizeUrl(state, verifier) is not { } authorize)
        {
            return OdaEndpoints.NotConfigured();
        }

        flow.Issue(
            http.Response,
            new OdaFlowState(
                verifier, state, householdId, default, userId, time.GetUtcNow().Add(OdaFlowCookie.Lifetime),
                OdaFlowKind.Sync, includeStaples, includeExisting, offset));
        return Results.Ok(new OdaSyncStartResponse(authorize.AbsoluteUri, selection.Selected.Count, selection.NotCheckedCount));
    }

    private static async Task<IResult> Review(
        Guid householdId, OdaCompleteRequest? request, HttpContext http, RequestLocale locale, ICurrentUser user, AppDbContext db,
        OdaFlowCookie flow, OdaSyncAction action, IOptions<OdaOptions> options, CancellationToken cancellationToken)
    {
        if (!options.Value.IsConfigured || user.UserId is not { } userId)
        {
            return OdaEndpoints.NotConfigured();
        }

        // Consumes the flow state first: it is single use whatever happens next, and no call to Oda is made without it.
        if (flow.Take(http, request?.State, householdId, default, userId, OdaFlowKind.Sync) is not { } state)
        {
            return AppProblems.Problem(
                StatusCodes.Status400BadRequest, AppProblems.OdaStateInvalid, "Sign-in expired", "Start again from the beginning.");
        }

        if (!string.IsNullOrWhiteSpace(request?.Error))
        {
            return Results.Ok(new OdaSyncReviewResponse("cancelled", null, OdaHistoryStatus.Skipped, [], 0, ConnectionClosed: true, ConnectionMessage: null));
        }

        var code = request?.Code;
        if (string.IsNullOrWhiteSpace(code))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["code is required."] });
        }

        // The options come from the cookie, never from the body. Read through the filtered sets before Oda is called.
        var (selection, existing) = await SelectAsync(db, locale.Code, state.IncludeStaples, state.IncludeExisting, state.Offset, options.Value, cancellationToken);
        if (selection.Selected.Count == 0)
        {
            return NothingToSync();
        }

        var outcome = await action.RunAsync(householdId, userId, new OdaSecret(code), new OdaSecret(state.Verifier), selection.Selected, existing, cancellationToken);
        if (outcome.Result is not { } result)
        {
            return AppProblems.Problem(
                StatusCodes.Status502BadGateway,
                AppProblems.OdaUnavailable,
                "Oda is not available",
                "The products could not be checked right now. Try again later.",
                new Dictionary<string, object?>
                {
                    ["connectionClosed"] = outcome.ConnectionClosed,
                    ["connectionMessage"] = OdaEndpoints.ConnectionMessage(outcome.ConnectionClosed),
                });
        }

        return Results.Ok(new OdaSyncReviewResponse(
            result.Stopped is null ? "done" : "partial",
            result.Stopped switch
            {
                SyncStopped.Time => OdaSyncStopped.Time,
                SyncStopped.ProviderLimit => OdaSyncStopped.OdaLimit,
                SyncStopped.ProviderError => OdaSyncStopped.OdaError,
                _ => null,
            },
            result.UsuallyBought switch
            {
                UsuallyBoughtStatus.Used => OdaHistoryStatus.Used,
                UsuallyBoughtStatus.Unavailable => OdaHistoryStatus.Unavailable,
                _ => OdaHistoryStatus.Skipped,
            },
            [.. result.Rows.Select(ToResponse)],
            selection.NotCheckedCount + (selection.Selected.Count - result.SearchedCount),
            outcome.ConnectionClosed,
            OdaEndpoints.ConnectionMessage(outcome.ConnectionClosed)));
    }

    private static OdaSyncRowResponse ToResponse(SyncRow row) => new(
        row.IngredientId,
        row.IngredientName,
        row.Category switch
        {
            SyncCategory.UsuallyBought => OdaSyncCategory.UsuallyBought,
            SyncCategory.Suggestion => OdaSyncCategory.Suggestion,
            SyncCategory.Current => OdaSyncCategory.Current,
            _ => OdaSyncCategory.NotFound,
        },
        row.Reason switch
        {
            SyncNotFoundReason.NoMatch => OdaSyncReason.NoMatch,
            SyncNotFoundReason.Unavailable => OdaSyncReason.Unavailable,
            _ => null,
        },
        row.HasExistingChoice,
        row.SelectedProductId is null ? null : OdaEndpoints.ParseId(row.SelectedProductId),
        row.Preticked,
        [.. row.Options.Select(o => new OdaSyncOptionResponse(OdaEndpoints.ParseId(o.ProductId), o.ProductName, o.IsUsual, o.IsCurrent))]);

    private static IResult NothingToSync() =>
        AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.OdaSyncNothingToSync,
            "Nothing to sync",
            "No ingredient of the household's recipes is left to check with these options.");

    /// <summary>
    /// The ingredients of one run, from the tenant-filtered sets of the household in the route: the primary ingredient of every recipe line,
    /// named in the request language, with the household's current choices. Also the choices of the selected ingredients (product ids as
    /// text) for the re-check. An in-memory result of this request only.
    /// </summary>
    private static async Task<(SyncSelection Selection, IReadOnlyDictionary<Guid, string> Existing)> SelectAsync(
        AppDbContext db, string locale, bool includeStaples, bool includeExisting, int offset, OdaOptions options, CancellationToken cancellationToken)
    {
        var pairs = await db.RecipeIngredients.AsNoTracking().Select(r => new { r.RecipeId, r.IngredientId }).Distinct().ToListAsync(cancellationToken);
        var recipeCounts = pairs.GroupBy(p => p.IngredientId).ToDictionary(g => g.Key, g => g.Count());
        var ids = recipeCounts.Keys.ToList();
        var named = await db.Ingredients.AsNoTracking().Where(i => ids.Contains(i.Id))
            .WithNames(db, CatalogTranslation.KindIngredient, locale)
            .Select(n => new { n.Row.Id, n.Name, n.Row.IsStaple })
            .ToListAsync(cancellationToken);
        var choices = (await db.PreferredOdaProducts.AsNoTracking().Select(p => new { p.IngredientId, p.OdaProductId }).ToListAsync(cancellationToken))
            .ToDictionary(p => p.IngredientId, p => p.OdaProductId);

        var selection = SyncSelector.Select(
            named.Select(n => new SyncIngredient(n.Id, n.Name, recipeCounts[n.Id], n.IsStaple, choices.ContainsKey(n.Id))),
            includeStaples,
            includeExisting,
            offset,
            Math.Max(1, options.MaxSyncIngredients),
            ShoppingListEndpoints.DisplayComparer(locale));
        var existing = selection.Selected.Where(i => i.HasChoice)
            .ToDictionary(i => i.IngredientId, i => choices[i.IngredientId].ToString(CultureInfo.InvariantCulture));
        return (selection, existing);
    }
}
