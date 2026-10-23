namespace Foodplanner.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Sanctioned write bypass (Q-006). Inside <see cref="ForHousehold"/> the write guard treats the given household
/// as the current household for Added and Modified tenant entities; Deleted entities and the read filter are
/// unaffected. Only ITenantResolution (T-011) may open a scope; T-012 enforces that with an architecture test.
/// Scoped per request, like the DbContext.
/// </summary>
public interface ITenantWriteScope
{
    /// <summary>The household of the open scope, or <c>null</c> when no scope is open.</summary>
    Guid? HouseholdId { get; }

    /// <summary>Opens a scope for <paramref name="householdId"/>. Scopes do not nest. Dispose to close it.</summary>
    IDisposable ForHousehold(Guid householdId);
}
