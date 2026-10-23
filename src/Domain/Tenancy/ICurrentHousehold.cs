namespace Foodplanner.Domain.Tenancy;

/// <summary>
/// The household the current request acts in. <c>null</c> means "no household context": tenant queries return
/// no rows and tenant writes are rejected. <see cref="Role"/> is the caller's role in that household, or
/// <c>null</c> when there is no context (including a caller who is not a member). Only the authorization
/// handler and the implementation read <see cref="Role"/> (T-011); keep implementations light, because the
/// cached EF model holds the first context's instance.
/// </summary>
public interface ICurrentHousehold
{
    Guid? HouseholdId { get; }

    HouseholdRole? Role { get; }
}
