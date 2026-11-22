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
public sealed record OpenInviteResponse(Guid Id, HouseholdRole Role, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, Guid? CreatedByUserId);

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

        var applied = await ApplyRoleAsync(db, userId, role, cancellationToken);
        if (applied.Result is { } refused)
        {
            return refused;
        }

        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(await ToResponseAsync(db, applied.Membership!, cancellationToken));
    }

    /// <summary>
    /// Sets a member's role of the current household (T-048). Must run inside the caller's transaction, after
    /// <see cref="HouseholdManagementEndpoints.LockHouseholdAsync"/>: that lock is what serialises two Owner changes, so the
    /// last-Owner check below cannot race. Looks the member up through the tenant filter (another household's user is not found)
    /// and refuses to demote the last Owner. Shared by the Owner endpoint and the support promotion (T-096): the same rules.
    /// </summary>
    internal static async Task<(IResult? Result, Membership? Membership)> ApplyRoleAsync(
        AppDbContext db, Guid userId, HouseholdRole role, CancellationToken cancellationToken)
    {
        var membership = await db.Memberships.SingleOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        if (membership is null)
        {
            return (HouseholdManagementEndpoints.MemberGone(), null);
        }

        if (membership.Role == HouseholdRole.Owner && role != HouseholdRole.Owner
            && !await db.Memberships.AnyAsync(m => m.Role == HouseholdRole.Owner && m.UserId != userId, cancellationToken))
        {
            return (AppProblems.Problem(
                StatusCodes.Status409Conflict,
                HouseholdManagementEndpoints.LastOwner,
                "Last owner cannot be demoted",
                "Make another member an Owner first, or delete the household."), null);
        }

        membership.ChangeRole(role);
        await db.SaveChangesAsync(cancellationToken);
        return (null, membership);
    }

    /// <summary>The member as the Owner endpoint returns it.</summary>
    internal static async Task<MemberResponse> ToResponseAsync(AppDbContext db, Membership membership, CancellationToken cancellationToken)
    {
        var email = await db.Users.AsNoTracking().Where(u => u.Id == membership.UserId).Select(u => u.Email).SingleOrDefaultAsync(cancellationToken);
        var joinedAt = db.Entry(membership).Property<DateTimeOffset>("CreatedAt").CurrentValue;
        return new MemberResponse(membership.UserId, email, membership.Role, joinedAt);
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
        return await RevokeOpenInviteAsync(db, inviteId, time.GetUtcNow(), cancellationToken) ? Results.NoContent() : InviteNotFoundProblem();
    }

    /// <summary>
    /// Ends an open invite of the current household (T-048). False when there is none with that id (unknown, not open, or another
    /// household's: all the same answer). Shared by the Owner endpoint and the support fix (T-096): the same rule.
    /// </summary>
    internal static async Task<bool> RevokeOpenInviteAsync(AppDbContext db, Guid inviteId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var invite = await db.Invites.SingleOrDefaultAsync(i => i.Id == inviteId && i.AcceptedAt == null && i.ExpiresAt > now, cancellationToken);
        if (invite is null)
        {
            return false;
        }

        invite.Revoke(now);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    internal static IResult InviteNotFoundProblem() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, InviteNotFound, "Invite not found", "There is no open invite with that id in this household.");
}
