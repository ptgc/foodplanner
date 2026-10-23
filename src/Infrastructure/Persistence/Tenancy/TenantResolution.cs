using Foodplanner.Domain.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Infrastructure.Persistence.Tenancy;

/// <summary>The one sanctioned filter bypass and write scope user. See <see cref="ITenantResolution"/>.</summary>
internal sealed class TenantResolution(AppDbContext db, ITenantWriteScope writeScope) : ITenantResolution
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    public async Task<IReadOnlyList<HouseholdMembership>> GetMembershipsForUser(Guid userId, CancellationToken cancellationToken) =>
        await (from membership in db.Memberships.IgnoreQueryFilters([AppDbContext.TenantFilterName]).AsNoTracking()
               where membership.UserId == userId
               join household in db.Households.AsNoTracking() on membership.HouseholdId equals household.Id
               orderby household.Name, household.Id
               select new HouseholdMembership(household.Id, household.Name, membership.Role))
            .ToListAsync(cancellationToken);

    public async Task<HouseholdRole?> FindRole(Guid userId, Guid householdId, CancellationToken cancellationToken)
    {
        var roles = await db.Memberships.IgnoreQueryFilters([AppDbContext.TenantFilterName]).AsNoTracking()
            .Where(m => m.UserId == userId && m.HouseholdId == householdId)
            .Select(m => (HouseholdRole?)m.Role)
            .ToListAsync(cancellationToken);
        return roles.SingleOrDefault();
    }

    public Task<Invite?> FindInviteByTokenHash(byte[] tokenHash, CancellationToken cancellationToken) =>
        db.Invites.IgnoreQueryFilters([AppDbContext.TenantFilterName]).AsNoTracking().SingleOrDefaultAsync(i => i.TokenHash == tokenHash, cancellationToken);

    public async Task<HouseholdMembership> CreateHouseholdWithOwner(string name, Guid ownerUserId, CancellationToken cancellationToken)
    {
        var household = new Household(name);
        // The key is generated when the entity is tracked, so the scope can name the new household before saving.
        db.Households.Add(household);
        using (writeScope.ForHousehold(household.Id))
        {
            db.Memberships.Add(new Membership(household.Id, ownerUserId, HouseholdRole.Owner));
            // One SaveChanges is one transaction: both rows or neither.
            await db.SaveChangesAsync(cancellationToken);
        }

        return new HouseholdMembership(household.Id, household.Name, HouseholdRole.Owner);
    }

    public async Task<InviteRedemptionResult> AddMembershipFromInvite(
        byte[] tokenHash, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var invalid = new InviteRedemptionResult(InviteRedemptionOutcome.InvalidInvite);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var invite = await db.Invites.IgnoreQueryFilters([AppDbContext.TenantFilterName]).AsNoTracking()
            .Where(i => i.TokenHash == tokenHash)
            .Select(i => new { i.Id, i.HouseholdId, i.Role, i.ExpiresAt, i.AcceptedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (invite is null || invite.AcceptedAt is not null || invite.ExpiresAt <= now)
        {
            return invalid;
        }

        var alreadyMember = new InviteRedemptionResult(InviteRedemptionOutcome.AlreadyMember);
        if (await db.Memberships.IgnoreQueryFilters([AppDbContext.TenantFilterName])
                .AnyAsync(m => m.HouseholdId == invite.HouseholdId && m.UserId == userId, cancellationToken))
        {
            return alreadyMember;
        }

        // The single-use gate: the UPDATE matches only an unaccepted, unexpired invite, and SQL Server serialises
        // concurrent updates of the row, so exactly one caller sees one affected row.
        var consumed = await db.Invites.IgnoreQueryFilters([AppDbContext.TenantFilterName])
            .Where(i => i.Id == invite.Id && i.AcceptedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(i => i.AcceptedAt, (DateTimeOffset?)now)
                    .SetProperty(i => i.AcceptedByUserId, (Guid?)userId)
                    .SetProperty(i => EF.Property<DateTimeOffset>(i, TenancyModelConventions.UpdatedAt), now),
                cancellationToken);
        if (consumed != 1)
        {
            return invalid;
        }

        try
        {
            using (writeScope.ForHousehold(invite.HouseholdId))
            {
                db.Memberships.Add(new Membership(invite.HouseholdId, userId, invite.Role));
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: UniqueIndexViolation or UniqueConstraintViolation })
        {
            // The same user joined this household concurrently. Dispose rolls back, so the invite stays unused.
            db.ChangeTracker.Clear();
            return alreadyMember;
        }

        return new InviteRedemptionResult(InviteRedemptionOutcome.Joined, invite.HouseholdId, invite.Role);
    }
}
