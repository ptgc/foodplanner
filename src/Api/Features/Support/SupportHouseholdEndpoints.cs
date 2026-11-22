using Foodplanner.Api.Authorization;
using Foodplanner.Api.Features.Auth;
using Foodplanner.Api.Features.Households;
using Foodplanner.Api.Features.MealPlans;
using Foodplanner.Api.Features.ShoppingLists;
using Foodplanner.Api.Localization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Support;
using Foodplanner.Domain.Support;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Support;

/// <summary>
/// The household overview. <c>Plan</c> is empty until household plans exist (T-123 fills it); today no plan is stored.
/// </summary>
public sealed record SupportHouseholdOverview(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    string? Plan,
    int MemberCount,
    int OwnerCount,
    int OpenInviteCount,
    int ProfileCount,
    int RecipeCount,
    int MealPlanCount);

/// <summary>A member with the PIN lock state read from <c>UserPin</c> (no hash, no failed count).</summary>
public sealed record SupportMemberItem(
    Guid UserId, string? Email, HouseholdRole Role, DateTimeOffset JoinedAt, bool PinLocked, DateTimeOffset? PinLockedUntil);

/// <summary><c>Status</c> is Open, Accepted or Expired (an invite ended by support or by a leaving creator reads as Expired). Never a token or hash.</summary>
public sealed record SupportInviteItem(
    Guid Id, HouseholdRole Role, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? AcceptedAt, Guid? CreatedByUserId);

public sealed record SupportProfileItem(Guid Id, string DisplayName, string Avatar);

public sealed record SupportRecipeItem(Guid Id, string Title, DateTimeOffset CreatedAt);

public sealed record SupportRecipePage(SupportRecipeItem[] Items, int Page, int PageSize, int Total);

/// <summary>
/// What support may do inside ONE household, under <c>/api/support/households/{targetHouseholdId}</c>. The household the queries
/// run in is the SESSION's (<see cref="ICurrentHousehold"/>, set by the support middleware from the session row), never the route
/// value: handlers do not read the route id. The four writes are named fixes that call the same code as the Owner and Adult
/// endpoints they mirror; there is no generic edit, no impersonation and no membership change besides promoting to Owner.
/// </summary>
public static class SupportHouseholdEndpoints
{
    public const int MaxRecipePageSize = 50;
    public const int MaxInvites = 50;

    public static IEndpointRouteBuilder MapSupportHouseholdEndpoints(this IEndpointRouteBuilder support)
    {
        var household = support.MapGroup($"/households/{{{SupportRoutes.RouteParameter}:guid}}");
        household.WithMetadata(SupportScopedMetadata.Instance);
        household.RequireAuthorization(Policies.SupportSessionRead);

        household.MapGet("", Overview)
            .WithMetadata(Audited(SupportActions.ViewOverview, SupportActionKind.View))
            .WithName("GetSupportHouseholdOverview")
            .Produces<SupportHouseholdOverview>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        household.MapGet("/members", Members)
            .WithMetadata(Audited(SupportActions.ViewMembers, SupportActionKind.View))
            .WithName("ListSupportHouseholdMembers")
            .Produces<SupportMemberItem[]>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        household.MapGet("/invites", Invites)
            .WithMetadata(Audited(SupportActions.ViewInvites, SupportActionKind.View))
            .WithName("ListSupportHouseholdInvites")
            .Produces<SupportInviteItem[]>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        household.MapGet("/profiles", Profiles)
            .WithMetadata(Audited(SupportActions.ViewProfiles, SupportActionKind.View))
            .WithName("ListSupportHouseholdProfiles")
            .Produces<SupportProfileItem[]>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        household.MapGet("/meal-plans/{weekStart}", MealPlan)
            .WithMetadata(Audited(SupportActions.ViewMealPlan, SupportActionKind.View))
            .WithName("GetSupportHouseholdMealPlan")
            .Produces<MealPlanResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        household.MapGet("/shopping-lists/{weekStart}", ShoppingList)
            .WithMetadata(Audited(SupportActions.ViewShoppingList, SupportActionKind.View))
            .WithName("GetSupportHouseholdShoppingList")
            .Produces<ShoppingListResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        household.MapGet("/recipes", Recipes)
            .WithMetadata(Audited(SupportActions.ViewRecipes, SupportActionKind.View))
            .WithName("ListSupportHouseholdRecipes")
            .Produces<SupportRecipePage>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        household.MapGet("/access-log", AccessLog)
            .WithMetadata(Audited(SupportActions.ViewAccessLog, SupportActionKind.View))
            .WithName("GetSupportHouseholdAccessLog")
            .Produces<SupportAdminAccessPage>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        household.MapDelete("/invites/{inviteId:guid}", CancelInvite)
            .RequireAuthorization(Policies.SupportSessionWrite)
            .WithMetadata(Audited(SupportActions.InviteCancel, SupportActionKind.Write))
            .WithName("CancelSupportHouseholdInvite")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        household.MapPut("/members/{userId:guid}/role", PromoteToOwner)
            .RequireAuthorization(Policies.SupportSessionWrite)
            .WithMetadata(Audited(SupportActions.MemberPromoteOwner, SupportActionKind.Write))
            .WithName("PromoteSupportHouseholdMemberToOwner")
            .Produces<MemberResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        household.MapPost("/members/{userId:guid}/pin-unlock", UnlockPin)
            .RequireAuthorization(Policies.SupportSessionWrite)
            .WithMetadata(Audited(SupportActions.MemberPinUnlock, SupportActionKind.Write))
            .WithName("ClearSupportHouseholdMemberPinLock")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        household.MapDelete("/meal-plans/{weekStart}/lock", UnlockWeek)
            .RequireAuthorization(Policies.SupportSessionWrite)
            .WithMetadata(Audited(SupportActions.WeekUnlock, SupportActionKind.Write))
            .WithName("UnlockSupportHouseholdMealPlanWeek")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return support;
    }

    private static SupportAuditedMetadata Audited(string action, SupportActionKind kind) => new(action, kind);

    private static async Task<IResult> Overview(ICurrentHousehold current, AppDbContext db, TimeProvider time, SupportAuditScope audit, CancellationToken cancellationToken)
    {
        if (current.HouseholdId is not { } householdId)
        {
            return Refused();
        }

        var now = time.GetUtcNow();
        var household = await db.Households.AsNoTracking().Where(h => h.Id == householdId)
            .Select(h => new { h.Id, h.Name, CreatedAt = EF.Property<DateTimeOffset>(h, "CreatedAt") })
            .FirstOrDefaultAsync(cancellationToken);
        if (household is null)
        {
            return AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.HouseholdNotFound, "Household not found");
        }

        // Every count is a tenant-filtered query: it sees this household only.
        var overview = new SupportHouseholdOverview(
            household.Id,
            household.Name,
            household.CreatedAt,
            Plan: null,
            await db.Memberships.CountAsync(cancellationToken),
            await db.Memberships.CountAsync(m => m.Role == HouseholdRole.Owner, cancellationToken),
            await db.Invites.CountAsync(i => i.AcceptedAt == null && i.ExpiresAt > now, cancellationToken),
            await db.MemberProfiles.CountAsync(cancellationToken),
            await db.Recipes.CountAsync(cancellationToken),
            await db.MealPlans.CountAsync(cancellationToken));
        audit.Detail("members", overview.MemberCount);
        return Results.Ok(overview);
    }

    private static async Task<IResult> Members(AppDbContext db, TimeProvider time, SupportAuditScope audit, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var rows = await (from m in db.Memberships.AsNoTracking()
                          join u in db.Users.AsNoTracking() on m.UserId equals u.Id into users
                          from u in users.DefaultIfEmpty()
                          join p in db.UserPins.AsNoTracking() on m.UserId equals p.UserId into pins
                          from p in pins.DefaultIfEmpty()
                          orderby EF.Property<DateTimeOffset>(m, "CreatedAt"), m.Id
                          select new
                          {
                              m.UserId,
                              u.Email,
                              m.Role,
                              JoinedAt = EF.Property<DateTimeOffset>(m, "CreatedAt"),
                              LockedUntil = (DateTimeOffset?)p.LockedUntil,
                          }).ToListAsync(cancellationToken);
        var members = rows
            .Select(r => new SupportMemberItem(
                r.UserId, r.Email, r.Role, r.JoinedAt, r.LockedUntil is { } until && until > now, r.LockedUntil is { } open && open > now ? open : null))
            .ToArray();
        audit.Detail("count", members.Length);
        return Results.Ok(members);
    }

    private static async Task<IResult> Invites(AppDbContext db, TimeProvider time, SupportAuditScope audit, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var rows = await db.Invites.AsNoTracking()
            .OrderByDescending(i => EF.Property<DateTimeOffset>(i, "CreatedAt")).ThenBy(i => i.Id)
            .Take(MaxInvites)
            .Select(i => new { i.Id, i.Role, i.ExpiresAt, i.AcceptedAt, i.CreatedByUserId, CreatedAt = EF.Property<DateTimeOffset>(i, "CreatedAt") })
            .ToListAsync(cancellationToken);
        var invites = rows
            .Select(i => new SupportInviteItem(
                i.Id, i.Role, i.AcceptedAt is not null ? "Accepted" : i.ExpiresAt > now ? "Open" : "Expired", i.CreatedAt, i.ExpiresAt, i.AcceptedAt, i.CreatedByUserId))
            .ToArray();
        audit.Detail("count", invites.Length);
        return Results.Ok(invites);
    }

    private static async Task<IResult> Profiles(AppDbContext db, SupportAuditScope audit, CancellationToken cancellationToken)
    {
        var profiles = await db.MemberProfiles.AsNoTracking()
            .OrderBy(p => p.DisplayName).ThenBy(p => p.Id)
            .Select(p => new SupportProfileItem(p.Id, p.DisplayName, p.Avatar))
            .ToArrayAsync(cancellationToken);
        audit.Detail("count", profiles.Length);
        return Results.Ok(profiles);
    }

    private static async Task<IResult> MealPlan(string weekStart, AppDbContext db, SupportAuditScope audit, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!MealPlanEndpoints.TryParseWeek(weekStart, errors, out var monday))
        {
            return Results.ValidationProblem(errors);
        }

        var plan = await MealPlanEndpoints.ReadWeekAsync(db, monday, cancellationToken);
        audit.Detail("weekStart", monday.ToString("yyyy-MM-dd"));
        audit.Detail("entries", plan.Entries.Length);
        return Results.Ok(plan);
    }

    private static async Task<IResult> ShoppingList(
        string weekStart, RequestLocale locale, AppDbContext db, SupportAuditScope audit, CancellationToken cancellationToken)
    {
        if (!ShoppingListEndpoints.IsWeekStart(weekStart, out var monday))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["weekStart"] = ["The week must be the Monday of an ISO week, as yyyy-MM-dd."],
            });
        }

        // No viewer: the list is in the fixed section order, not a member's chosen store.
        var list = await ShoppingListEndpoints.ReadAsync(db, monday, locale.Code, userId: null, cancellationToken);
        audit.Detail("weekStart", monday.ToString("yyyy-MM-dd"));
        audit.Detail("items", list.Main.Length + list.CheckYouHave.Length);
        return Results.Ok(list);
    }

    private static async Task<IResult> Recipes(int? page, int? pageSize, AppDbContext db, SupportAuditScope audit, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var (pageNumber, size) = SupportEndpoints.Paging(page, pageSize, errors, MaxRecipePageSize);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        // Titles only: no recipe detail, no images, no export.
        var total = await db.Recipes.CountAsync(cancellationToken);
        var items = await db.Recipes.AsNoTracking()
            .OrderBy(r => r.Title).ThenBy(r => r.Id)
            .Skip((pageNumber - 1) * size).Take(size)
            .Select(r => new SupportRecipeItem(r.Id, r.Title, EF.Property<DateTimeOffset>(r, "CreatedAt")))
            .ToArrayAsync(cancellationToken);
        audit.Detail("page", pageNumber);
        audit.Detail("resultCount", items.Length);
        return Results.Ok(new SupportRecipePage(items, pageNumber, size, total));
    }

    private static async Task<IResult> AccessLog(
        int? page, int? pageSize, ICurrentHousehold current, SupportAccessLog log, SupportAuditScope audit, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var (pageNumber, size) = SupportEndpoints.Paging(page, pageSize, errors, SupportAccessLog.DefaultPageSize);
        if (errors.Count > 0 || current.HouseholdId is not { } householdId)
        {
            return errors.Count > 0 ? Results.ValidationProblem(errors) : Refused();
        }

        var result = await log.ForAdminAsync(householdId, pageNumber, Math.Min(size, SupportAccessLog.MaxPageSize), cancellationToken);
        audit.Detail("resultCount", result.Items.Length);
        return Results.Ok(result);
    }

    private static async Task<IResult> CancelInvite(
        Guid inviteId, AppDbContext db, TimeProvider time, SupportAuditScope audit, CancellationToken cancellationToken)
    {
        audit.Detail("inviteId", inviteId);
        return await HouseholdMemberEndpoints.RevokeOpenInviteAsync(db, inviteId, time.GetUtcNow(), cancellationToken)
            ? Results.NoContent()
            : HouseholdMemberEndpoints.InviteNotFoundProblem();
    }

    /// <summary>Promotes an EXISTING member to Owner. Support never demotes, removes or adds a member. Same household row lock and rules as the Owner endpoint.</summary>
    private static async Task<IResult> PromoteToOwner(
        Guid userId, ChangeMemberRoleRequest? request, ICurrentHousehold current, AppDbContext db, SupportAuditScope audit, CancellationToken cancellationToken)
    {
        if (current.HouseholdId is not { } householdId)
        {
            return Refused();
        }

        if (!HouseholdRoles.TryParse(request?.Role, out var requested) || requested != HouseholdRole.Owner)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["role"] = ["Support can only make a member an Owner."],
            });
        }

        audit.Detail("userId", userId);
        await HouseholdManagementEndpoints.LockHouseholdAsync(db, householdId, cancellationToken);
        var applied = await HouseholdMemberEndpoints.ApplyRoleAsync(db, userId, HouseholdRole.Owner, cancellationToken);
        return applied.Result ?? Results.Ok(await HouseholdMemberEndpoints.ToResponseAsync(db, applied.Membership!, cancellationToken));
    }

    /// <summary>Clears the PIN attempt counter and lock of a member of the household. Never reads, sets or removes a PIN.</summary>
    private static async Task<IResult> UnlockPin(
        Guid userId, AppDbContext db, PinService pins, SupportAuditScope audit, CancellationToken cancellationToken)
    {
        audit.Detail("userId", userId);

        // Through the tenant filter: a user who belongs only to another household is not found.
        if (!await db.Memberships.AsNoTracking().AnyAsync(m => m.UserId == userId, cancellationToken))
        {
            return HouseholdManagementEndpoints.MemberGone();
        }

        await pins.ClearLockoutAsync(userId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> UnlockWeek(
        string weekStart, ICurrentHousehold current, AppDbContext db, IMealPlanNotifier notifier, ILogger<Foodplanner.Domain.MealPlans.MealPlan> logger,
        SupportAuditScope audit, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!MealPlanEndpoints.TryParseWeek(weekStart, errors, out var monday))
        {
            return Results.ValidationProblem(errors);
        }

        if (current.HouseholdId is not { } householdId)
        {
            return Refused();
        }

        audit.Detail("weekStart", monday.ToString("yyyy-MM-dd"));
        if (await MealPlanEndpoints.UnlockWeekRowAsync(db, monday, cancellationToken))
        {
            // The live signal goes out after the audit row and the unlock are committed.
            audit.AfterCommit(ct => MealPlanEndpoints.NotifyAsync(
                notifier, logger, new MealPlanChange(householdId, monday, MealPlanChangeKind.Unlocked, null), ct));
        }

        return Results.NoContent();
    }

    /// <summary>The policy guarantees a session household; a missing one is a wiring bug, so refuse rather than guess.</summary>
    private static IResult Refused() =>
        AppProblems.Problem(StatusCodes.Status403Forbidden, AppProblems.Forbidden, "Forbidden");
}
