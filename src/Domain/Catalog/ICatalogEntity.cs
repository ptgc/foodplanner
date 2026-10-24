namespace Foodplanner.Domain.Catalog;

/// <summary>
/// A global-or-household catalog entity (ADR 0005): <see cref="HouseholdId"/> null is a global row, read-only to every
/// household and written only by the seed (migration data); a value is a row the household created itself.
/// This is deliberately not <c>ITenantEntity</c>: that one promises a non-null owner. Infrastructure gives a catalog entity
/// the same <c>Tenant</c> query filter in the form "global or mine", and the save guard (TenancySaveChangesInterceptor)
/// refuses any request-time write to a global row and any add without a household.
/// Implementations expose no public setter. A new row must be created with its household id or <see cref="Guid.Empty"/>
/// (Infrastructure then stamps the current household on Add); a new row with a null household id is refused, so request
/// code can never create a global row by forgetting to set it.
/// </summary>
public interface ICatalogEntity
{
    Guid? HouseholdId { get; }
}
