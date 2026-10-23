using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Redeems a plain invite token for a signed-in user. Used by <c>POST /api/invites/{token}/accept</c> and, later, by
/// the external-login callback (T-010). Any signed-in user holding the token may redeem it (Q-005).
/// </summary>
public interface IInviteRedemption
{
    Task<InviteRedemptionResult> AcceptAsync(string token, Guid userId, CancellationToken cancellationToken = default);
}

internal sealed class InviteRedemption(ITenantResolution resolution, TimeProvider time) : IInviteRedemption
{
    public Task<InviteRedemptionResult> AcceptAsync(string token, Guid userId, CancellationToken cancellationToken = default)
    {
        // Input that cannot be one of our tokens is rejected without a lookup, with the same outcome as an unknown token.
        if (!InviteToken.HasValidShape(token))
        {
            return Task.FromResult(new InviteRedemptionResult(InviteRedemptionOutcome.InvalidInvite));
        }

        return resolution.AddMembershipFromInvite(InviteToken.Hash(token), userId, time.GetUtcNow(), cancellationToken);
    }
}
