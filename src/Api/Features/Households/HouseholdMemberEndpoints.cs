using Foodplanner.Api.Authorization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Households;

public sealed record MemberResponse(Guid UserId, string? Email, HouseholdRole Role, DateTimeOffset JoinedAt);

public sealed record ChangeMemberRoleRequest(string? Role);

/// <summary>An open invite. Never carries the token or its hash: the plain token is shown once, at creation.</summary>
public sealed record OpenInviteResponse(Guid Id, HouseholdRole Role, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, Guid CreatedByUserId);

/// <summary>
/// Member and invite management for the Owner (T-048). Routes are household-scoped; every query runs through the tenant
/// filter of that household, so another household's members and invites are simply not found (404) and never listed.
/// Removing a member is <c>DELETE .../members/{userId}</c> in <see cref="HouseholdManagementEndpoints"/> (T-044).
/// </summary>
public static class HouseholdMemberEndpoints
{
    public const string InviteNotFound = "invite-not-found";

    public static IEndpointRouteBuilder MapHouseholdMemberEndpoints(this IEndpointRouteBuilder api)
    {
        var household = api.MapHouseholdScopedGroup();

        household.MapGet("/members", ListMembers)
            .RequireAuthorization(Policies.HouseholdOwner)
            .WithName("ListHouseholdMembers")
            .Produces<MemberResponse[]>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        household.MapPut("/members/{userId:guid}/role", ChangeRole)
            .RequireAuthorization(Policies.HouseholdOwner)
            .WithName("ChangeHouseholdMemberRole")
            .Produces<MemberResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        household.MapGet("/invites", ListOpenInvites)
            .RequireAuthorization(Policies.HouseholdOwner)
            .WithName("ListOpenInvites")
            .Produces<OpenInviteResponse[]>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        household.MapDelete("/invites/{inviteId:guid}", RevokeInvite)
            .RequireAuthorization(Policies.HouseholdOwner)
            .WithName("RevokeInvite")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }

    private static async Task<IResult> ListMembers(ICurrentHousehold current, AppDbContext db, CancellationToken cancellationToken)
    {
        if (current.HouseholdId is null)
        {
            return HouseholdManagementEndpoints.Forbidden();
        }

        // Memberships are tenant-filtered; the address comes from the global identity table.
        var members = await (from m in db.Memberships.AsNoTracking()
                             join u in db.Users.AsNoTracking() on m.UserId equals u.Id into users
                             from u in users.DefaultIfEmpty()
                             orderby EF.Property<DateTimeOffset>(m, "CreatedAt"), m.Id
                             select new MemberResponse(m.UserId, u.Email, m.Role, EF.Property<DateTimeOffset>(m, "CreatedAt")))
            .ToListAsync(cancellationToken);
        return Results.Ok(members);
    }

    private static async Task<IResult> ChangeRole(
        Guid userId,
        ChangeMemberRoleRequest? request,
        ICurrentUser user,
        ICurrentHousehold current,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (current.HouseholdId is not { } householdId)
        {
            return HouseholdManagementEndpoints.Forbidden();
        }

        if (!HouseholdRoles.TryParse(request?.Role, out var role))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["role"] = ["The role must be Owner, Adult or Child."],
            });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The same household row lock as leave and remove: two Owners demoting each other at once are serialised, so
        // the second one sees the first's result and is refused instead of leaving the household without an Owner.
        await HouseholdManagementEndpoints.LockHouseholdAsync(db, householdId, cancellationToken);

        if (!await HouseholdManagementEndpoints.CallerStillOwnsAsync(user, db, cancellationToken))
        {
            return HouseholdManagementEndpoints.Forbidden();
        }

        var membership = await db.Memberships.SingleOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        if (membership is null)
        {
            return HouseholdManagementEndpoints.MemberGone();
        }

        if (membership.Role == HouseholdRole.Owner && role != HouseholdRole.Owner
            && !await db.Memberships.AnyAsync(m => m.Role == HouseholdRole.Owner && m.UserId != userId, cancellationToken))
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                HouseholdManagementEndpoints.LastOwner,
                "Last owner cannot be demoted",
                "Make another member an Owner first, or delete the household.");
        }

        membership.ChangeRole(role);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var email = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).SingleOrDefaultAsync(cancellationToken);
        var joinedAt = db.Entry(membership).Property<DateTimeOffset>("CreatedAt").CurrentValue;
        return Results.Ok(new MemberResponse(userId, email, membership.Role, joinedAt));
    }

    private static async Task<IResult> ListOpenInvites(
        ICurrentHousehold current, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (current.HouseholdId is null)
        {
            return HouseholdManagementEndpoints.Forbidden();
        }

        var now = time.GetUtcNow();
        var invites = await db.Invites.AsNoTracking()
            .Where(i => i.AcceptedAt == null && i.ExpiresAt > now)
            .OrderBy(i => EF.Property<DateTimeOffset>(i, "CreatedAt")).ThenBy(i => i.Id)
            .Select(i => new OpenInviteResponse(i.Id, i.Role, i.ExpiresAt, EF.Property<DateTimeOffset>(i, "CreatedAt"), i.CreatedByUserId))
            .ToListAsync(cancellationToken);
        return Results.Ok(invites);
    }

    private static async Task<IResult> RevokeInvite(
        Guid inviteId, ICurrentHousehold current, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (current.HouseholdId is null)
        {
            return HouseholdManagementEndpoints.Forbidden();
        }

        // Only open invites are found, as only they are listed; the tenant filter keeps it to this household. Invite.Revoke
        // moves ExpiresAt to now (the row stays), so the link answers 410 invite-invalid. A redemption committing between
        // this read and the save only has its (already accepted) invite's expiry moved: harmless, the membership stands.
        var now = time.GetUtcNow();
        var invite = await db.Invites.SingleOrDefaultAsync(i => i.Id == inviteId && i.AcceptedAt == null && i.ExpiresAt > now, cancellationToken);
        if (invite is null)
        {
            return AppProblems.Problem(StatusCodes.Status404NotFound, InviteNotFound, "Invite not found", "There is no open invite with that id in this household.");
        }

        invite.Revoke(now);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }
}
