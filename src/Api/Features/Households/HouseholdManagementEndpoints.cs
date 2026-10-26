using System.Text.Json;
using System.Text.Json.Serialization;
using Foodplanner.Api.Authorization;
using Foodplanner.Api.Features.Recipes;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Realtime;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Households;

/// <summary>
/// Leaving, removing members, deleting and exporting a household (T-044). All routes are household-scoped
/// (<c>/api/households/{householdId}/...</c>), so the household middleware has already resolved the caller's membership
/// and every query below runs through the tenant filter of THAT household.
/// <list type="bullet">
/// <item><c>DELETE .../</c> (HouseholdOwner) deletes the household and everything it owns.</item>
/// <item><c>POST .../leave</c> (HouseholdMember) removes the caller's own membership; the last Owner is refused.</item>
/// <item><c>DELETE .../members/{userId}</c> (HouseholdOwner) removes another member.</item>
/// <item><c>GET .../export</c> (HouseholdAdult) downloads the household's data as JSON.</item>
/// </list>
/// </summary>
public static class HouseholdManagementEndpoints
{
    public const string LastOwner = "last-owner";
    public const string CannotRemoveSelf = "cannot-remove-self";
    public const string OwnerCannotBeRemoved = "owner-cannot-be-removed";
    public const string MemberNotFound = "member-not-found";

    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public static IEndpointRouteBuilder MapHouseholdManagementEndpoints(this IEndpointRouteBuilder api)
    {
        var household = api.MapHouseholdScopedGroup();

        household.MapDelete("", DeleteHousehold)
            .RequireAuthorization(Policies.HouseholdOwner)
            .WithName("DeleteHousehold")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        household.MapDelete("/members/{userId:guid}", RemoveMember)
            .RequireAuthorization(Policies.HouseholdOwner)
            .WithName("RemoveHouseholdMember")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // The group's default policy (HouseholdMember) applies: any member, Child included, may leave.
        household.MapPost("/leave", LeaveHousehold)
            .WithName("LeaveHousehold")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        household.MapGet("/export", ExportHousehold)
            .RequireAuthorization(Policies.HouseholdAdult)
            .WithName("ExportHousehold")
            .Produces<HouseholdExportResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static async Task<IResult> DeleteHousehold(
        ICurrentHousehold current,
        AppDbContext db,
        IImageStore images,
        ILiveConnections live,
        ILogger<Household> logger,
        CancellationToken cancellationToken)
    {
        if (current.HouseholdId is not { } householdId)
        {
            return Forbidden();
        }

        // Household is the tenant root (no HouseholdId, no filter); the id is the one the policy proved the caller owns.
        // The database does the rest: every household-owned table cascades from Households, in one statement.
        var household = await db.Households.SingleOrDefaultAsync(h => h.Id == householdId, cancellationToken);
        if (household is null)
        {
            return Forbidden();
        }

        db.Households.Remove(household);
        await db.SaveChangesAsync(cancellationToken);

        // After the commit, best effort: files left behind are invisible orphans (nothing points at them), but are logged.
        await RecipeImageEndpoints.TryDeleteBlobsAsync(images, logger, RecipeImageBlobNames.HouseholdPrefix(householdId), cancellationToken);
        await live.CloseHouseholdAsync(householdId);
        return Results.NoContent();
    }

    private static async Task<IResult> LeaveHousehold(
        ICurrentUser user,
        ICurrentHousehold current,
        AppDbContext db,
        TimeProvider time,
        ILiveConnections live,
        CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId)
        {
            return AppProblems.Problem(StatusCodes.Status401Unauthorized, AppProblems.Unauthenticated, "Not signed in");
        }

        if (current.HouseholdId is not { } householdId)
        {
            return Forbidden();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Serialise with every other leave of this household: the next statement takes the row lock on the household,
        // held until commit, so two Owners leaving at once cannot both see "another Owner remains".
        await LockHouseholdAsync(db, householdId, cancellationToken);

        var membership = await db.Memberships.SingleOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        if (membership is null)
        {
            return MemberGone();
        }

        if (membership.Role == HouseholdRole.Owner
            && !await db.Memberships.AnyAsync(m => m.Role == HouseholdRole.Owner && m.UserId != userId, cancellationToken))
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                LastOwner,
                "Last owner cannot leave",
                "You are the only Owner. Transfer ownership to another member first, or delete the household.");
        }

        await RemoveMembershipAsync(db, membership, time.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await live.EvictUserFromHouseholdAsync(userId, householdId);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveMember(
        Guid userId,
        ICurrentUser user,
        ICurrentHousehold current,
        AppDbContext db,
        TimeProvider time,
        ILiveConnections live,
        CancellationToken cancellationToken)
    {
        if (current.HouseholdId is not { } householdId)
        {
            return Forbidden();
        }

        if (user.UserId == userId)
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                CannotRemoveSelf,
                "Cannot remove yourself",
                "Use leave to remove your own membership.");
        }

        // Through the tenant filter: a user who belongs only to another household is simply not found.
        var membership = await db.Memberships.SingleOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        if (membership is null)
        {
            return MemberGone();
        }

        if (membership.Role == HouseholdRole.Owner)
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                OwnerCannotBeRemoved,
                "An owner cannot be removed",
                "Owners leave on their own, or ownership is changed first.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RemoveMembershipAsync(db, membership, time.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await live.EvictUserFromHouseholdAsync(userId, householdId);
        return Results.NoContent();
    }

    private static async Task<IResult> ExportHousehold(
        ICurrentHousehold current, AppDbContext db, TimeProvider time, HttpContext http, CancellationToken cancellationToken)
    {
        if (current.HouseholdId is not { } householdId)
        {
            return Forbidden();
        }

        var household = await db.Households.AsNoTracking().SingleOrDefaultAsync(h => h.Id == householdId, cancellationToken);
        if (household is null)
        {
            return Forbidden();
        }

        var export = await HouseholdExporter.BuildAsync(db, householdId, household.Name, time.GetUtcNow(), cancellationToken);

        // Personal data: a download, never cached by shared caches.
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"foodplanner-household-{householdId:N}.json\"";
        http.Response.Headers.CacheControl = "private, no-store";
        return Results.Json(export, ExportJson);
    }

    /// <summary>
    /// Deletes the membership and ends the open invites its user created in this household (the links would otherwise
    /// keep admitting people on behalf of someone who is no longer a member). Invites are kept, only expired; invites
    /// already accepted, and everything the user picked or checked, stay as history. Must run inside a transaction.
    /// </summary>
    private static async Task RemoveMembershipAsync(AppDbContext db, Membership membership, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var userId = membership.UserId;
        var open = await db.Invites.Where(i => i.CreatedByUserId == userId && i.AcceptedAt == null && i.ExpiresAt > now).ToListAsync(cancellationToken);
        foreach (var invite in open)
        {
            invite.Revoke(now);
        }

        db.Memberships.Remove(membership);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A tracked no-op update of the household row: takes its row lock for the rest of the transaction.</summary>
    private static async Task LockHouseholdAsync(AppDbContext db, Guid householdId, CancellationToken cancellationToken)
    {
        var household = await db.Households.SingleAsync(h => h.Id == householdId, cancellationToken);
        db.Entry(household).State = EntityState.Modified;
        await db.SaveChangesAsync(cancellationToken);
        db.Entry(household).State = EntityState.Detached;
    }

    private static IResult Forbidden() =>
        AppProblems.Problem(StatusCodes.Status403Forbidden, AppProblems.Forbidden, "Forbidden");

    private static IResult MemberGone() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, MemberNotFound, "Member not found", "The user is not a member of this household.");
}
