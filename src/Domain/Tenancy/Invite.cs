using Foodplanner.Domain.Common;

namespace Foodplanner.Domain.Tenancy;

/// <summary>
/// A single-use, expiring bearer invite (Q-005). Only the SHA-256 hash of the token is stored. Redemption marks the
/// invite accepted; it is never deleted.
/// </summary>
public sealed class Invite : Entity, ITenantEntity
{
    private Invite()
    {
    }

    public Invite(Guid householdId, byte[] tokenHash, HouseholdRole role, DateTimeOffset expiresAt, Guid createdByUserId)
    {
        if (householdId == Guid.Empty)
        {
            throw new ArgumentException("An invite needs a household.", nameof(householdId));
        }

        if (!role.IsInvitable())
        {
            throw new ArgumentException("An invite grants Adult or Child only.", nameof(role));
        }

        HouseholdId = householdId;
        TokenHash = tokenHash;
        Role = role;
        ExpiresAt = expiresAt;
        CreatedByUserId = createdByUserId;
    }

    public Guid HouseholdId { get; private set; }

    public byte[] TokenHash { get; private set; } = [];

    public HouseholdRole Role { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public Guid? AcceptedByUserId { get; private set; }

    public bool IsUsable(DateTimeOffset now) => AcceptedAt is null && ExpiresAt > now;
}
