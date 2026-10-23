namespace Foodplanner.Domain.Tenancy;

/// <summary>
/// The household the current request acts in. <c>null</c> means "no household context": tenant queries return
/// no rows and tenant writes are rejected. T-011 adds the role and provides the real implementation.
/// </summary>
public interface ICurrentHousehold
{
    Guid? HouseholdId { get; }
}
