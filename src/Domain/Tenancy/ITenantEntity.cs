namespace Foodplanner.Domain.Tenancy;

/// <summary>
/// A household-owned entity (CLAUDE.md rule 1, ADR 0005). Reads are filtered to the current household and
/// writes are guarded by Infrastructure. Implementations expose no public setter: Infrastructure assigns the
/// value through the EF change tracker when the entity is added with an empty <see cref="HouseholdId"/>.
/// </summary>
public interface ITenantEntity
{
    Guid HouseholdId { get; }
}
