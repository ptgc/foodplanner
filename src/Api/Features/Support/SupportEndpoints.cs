using Foodplanner.Api.Authorization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Security;
using Foodplanner.Api.Support;
using Foodplanner.Domain.Support;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Support;

/// <summary>
/// Start a support session (T-096). <c>Mode</c> is <c>Read</c> or <c>Write</c>; <c>ReasonCategory</c> one of LostOwner, StuckInvite,
/// PinLockout, LockedWeek, DataProblem, Other; <c>ReasonNote</c> 10 to 500 characters. A write session needs the admin's own
/// <c>Password</c> again.
/// </summary>
public sealed record StartSupportSessionRequest(Guid? HouseholdId, string? Mode, string? ReasonCategory, string? ReasonNote, string? Password);

public sealed record SupportSessionResponse(
    Guid Id,
    Guid HouseholdId,
    string HouseholdName,
    SupportSessionMode Mode,
    SupportReasonCategory ReasonCategory,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt);

/// <summary>A household in the support list: only these three fields.</summary>
public sealed record SupportHouseholdItem(Guid Id, string Name, DateTimeOffset CreatedAt);

public sealed record SupportHouseholdPage(SupportHouseholdItem[] Items, int Page, int PageSize, int Total);

public sealed record SupportUserHousehold(Guid Id, string Name, HouseholdRole Role);

/// <summary>A user found by email prefix: only these fields, never a hash, a stamp or PIN data.</summary>
public sealed record SupportUserItem(Guid UserId, string Email, bool EmailConfirmed, SupportUserHousehold[] Households);

/// <summary>
/// System admin support (T-096). Everything is under <c>/api/support</c>, never under <c>/api/households/{householdId}</c>.
/// The group needs <see cref="Policies.SystemAdmin"/>; the household sub-group (see <see cref="SupportHouseholdEndpoints"/>) also
/// needs an active session for that household. Every endpoint declares its audit action; the shared filter writes the audit row.
/// </summary>
public static class SupportEndpoints
{
    public const int MinHouseholdSearchLength = 2;
    public const int MinUserSearchLength = 3;
    public const int MaxSearchLength = 100;
    public const int MaxResults = 20;

    public static IEndpointRouteBuilder MapSupportEndpoints(this IEndpointRouteBuilder api)
    {
        var support = api.MapGroup("/support");
        support.WithMetadata(SupportRouteMetadata.Instance);
        support.RequireAuthorization(Policies.SystemAdmin);
        support.AddEndpointFilter<SupportAuditFilter>();

        var sessions = support.MapGroup("/sessions");
        sessions.MapPost("", StartSession)
            .WithMetadata(new SupportAuditedMetadata(SupportActions.SessionStart, SupportActionKind.Session, transactional: false))
            .RequireRateLimiting(RateLimitPolicies.SupportSessionStart)
            .WithName("StartSupportSession")
            .Produces<SupportSessionResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        sessions.MapGet("/current", CurrentSession)
            .WithMetadata(new SupportAuditedMetadata(SupportActions.SessionView, SupportActionKind.Session))
            .WithName("GetCurrentSupportSession")
            .Produces<SupportSessionResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        sessions.MapDelete("/current", EndSession)
            .WithMetadata(new SupportAuditedMetadata(SupportActions.SessionEnd, SupportActionKind.Session))
            .WithName("EndSupportSession")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        support.MapGet("/households", SearchHouseholds)
            .WithMetadata(new SupportAuditedMetadata(SupportActions.SearchHouseholds, SupportActionKind.View))
            .RequireRateLimiting(RateLimitPolicies.SupportSearch)
            .WithName("SearchSupportHouseholds")
            .Produces<SupportHouseholdPage>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        support.MapGet("/users", SupportUserSearch)
            .WithMetadata(new SupportAuditedMetadata(SupportActions.SearchUsers, SupportActionKind.View))
            .RequireRateLimiting(RateLimitPolicies.SupportSearch)
            .WithName("SearchSupportUsers")
            .Produces<SupportUserItem[]>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        support.MapSupportHouseholdEndpoints();
        return api;
    }

    private static Task<IResult> StartSession(StartSupportSessionRequest? request, SupportSessionService sessions, CancellationToken cancellationToken) =>
        sessions.StartAsync(request, cancellationToken);

    private static Task<IResult> CurrentSession(SupportSessionService sessions, CancellationToken cancellationToken) =>
        sessions.CurrentAsync(cancellationToken);

    private static Task<IResult> EndSession(SupportSessionService sessions, CancellationToken cancellationToken) =>
        sessions.EndAsync(cancellationToken);

    /// <summary>
    /// Lists every household, paged, or searches by name (at least 2 characters) or by exact household id. At most 20 per page,
    /// ordered by name then id. <c>Households</c> is the tenant root: it is not filtered, so this needs no bypass.
    /// </summary>
    private static async Task<IResult> SearchHouseholds(
        string? search, int? page, int? pageSize, AppDbContext db, SupportAuditScope audit, CancellationToken cancellationToken)
    {
        var term = search?.Trim();
        var errors = new Dictionary<string, string[]>();
        if (!string.IsNullOrEmpty(term) && (term.Length < MinHouseholdSearchLength || term.Length > MaxSearchLength))
        {
            errors["search"] = [$"Search with {MinHouseholdSearchLength} to {MaxSearchLength} characters, or leave it empty to list all households."];
        }

        var (pageNumber, size) = Paging(page, pageSize, errors, MaxResults);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var query = db.Households.AsNoTracking();
        if (!string.IsNullOrEmpty(term))
        {
            query = Guid.TryParse(term, out var id)
                ? query.Where(h => h.Id == id || h.Name.Contains(term))
                : query.Where(h => h.Name.Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(h => h.Name).ThenBy(h => h.Id)
            .Skip((pageNumber - 1) * size).Take(size)
            .Select(h => new SupportHouseholdItem(h.Id, h.Name, EF.Property<DateTimeOffset>(h, "CreatedAt")))
            .ToArrayAsync(cancellationToken);

        audit.Detail("query", term);
        audit.Detail("page", pageNumber);
        audit.Detail("resultCount", items.Length);
        audit.Detail("total", total);
        return Results.Ok(new SupportHouseholdPage(items, pageNumber, size, total));
    }

    /// <summary>
    /// Users by email prefix (at least 3 characters, at most 20), with the households each belongs to (id, name, role only). The
    /// households come from <see cref="ITenantResolution.GetMembershipsForUser"/>, the one sanctioned cross-household read; this
    /// method is its second named caller (architecture test).
    /// </summary>
    private static async Task<IResult> SupportUserSearch(
        string? email, AppDbContext db, ITenantResolution resolution, SupportAuditScope audit, CancellationToken cancellationToken)
    {
        var term = email?.Trim();
        if (string.IsNullOrEmpty(term) || term.Length < MinUserSearchLength || term.Length > MaxSearchLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["email"] = [$"Search with {MinUserSearchLength} to {MaxSearchLength} characters of the email address."],
            });
        }

        var prefix = term.ToUpperInvariant();
        var users = await db.Users.AsNoTracking()
            .Where(u => u.NormalizedEmail != null && u.NormalizedEmail.StartsWith(prefix))
            .OrderBy(u => u.NormalizedEmail).ThenBy(u => u.Id)
            .Take(MaxResults)
            .Select(u => new { u.Id, u.Email, u.EmailConfirmed })
            .ToListAsync(cancellationToken);

        var items = new List<SupportUserItem>(users.Count);
        foreach (var user in users)
        {
            var memberships = await resolution.GetMembershipsForUser(user.Id, cancellationToken);
            items.Add(new SupportUserItem(
                user.Id, user.Email ?? string.Empty, user.EmailConfirmed,
                [.. memberships.Select(m => new SupportUserHousehold(m.HouseholdId, m.Name, m.Role))]));
        }

        audit.Detail("query", term);
        audit.Detail("resultCount", items.Count);
        return Results.Ok(items.ToArray());
    }

    /// <summary>Page number (default 1) and size (default <paramref name="max"/>, capped at it), with validation errors under <c>page</c> and <c>pageSize</c>.</summary>
    internal static (int Page, int PageSize) Paging(int? page, int? pageSize, Dictionary<string, string[]> errors, int max)
    {
        if (page is < 1)
        {
            errors["page"] = ["The page starts at 1."];
        }

        if (pageSize is < 1)
        {
            errors["pageSize"] = ["The page size must be at least 1."];
        }

        return (page is >= 1 ? page.Value : 1, Math.Min(pageSize is >= 1 ? pageSize.Value : max, max));
    }
}
