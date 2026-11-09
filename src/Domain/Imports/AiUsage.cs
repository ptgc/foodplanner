using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Imports;

/// <summary>Which UTC day and month a call is counted in (T-077). One place, so the counters and the limits agree.</summary>
public static class AiUsagePeriod
{
    public static DateOnly Day(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime);

    public static DateOnly Month(DateTimeOffset now) => new(now.UtcDateTime.Year, now.UtcDateTime.Month, 1);
}

/// <summary>
/// How many AI calls a household has made today and this month (T-077): one row per household. The counters roll over by
/// themselves: a row whose <see cref="Day"/> or <see cref="MonthStart"/> is not the current one counts as zero for that period.
/// Rows are written only by atomic conditional updates in Infrastructure (so two imports at once cannot both pass the last
/// call); the entity has no mutators on purpose. HouseholdId is assigned by Infrastructure.
/// </summary>
public sealed class AiHouseholdUsage : Entity, ITenantEntity
{
    private AiHouseholdUsage()
    {
    }

    /// <summary>The row for a household's first call: one call today and this month.</summary>
    public AiHouseholdUsage(DateTimeOffset now)
    {
        Day = AiUsagePeriod.Day(now);
        MonthStart = AiUsagePeriod.Month(now);
        DayCalls = 1;
        MonthCalls = 1;
    }

    public Guid HouseholdId { get; private set; }

    public DateOnly Day { get; private set; }

    public int DayCalls { get; private set; }

    public DateOnly MonthStart { get; private set; }

    public int MonthCalls { get; private set; }
}

/// <summary>
/// How many AI calls all households made in a month (T-077), for the global cap. Deliberately not a tenant entity: it holds one
/// number per month and no household data. One row per <see cref="MonthStart"/>.
/// </summary>
public sealed class AiGlobalUsage : Entity
{
    private AiGlobalUsage()
    {
    }

    public AiGlobalUsage(DateTimeOffset now)
    {
        MonthStart = AiUsagePeriod.Month(now);
        Calls = 1;
    }

    public DateOnly MonthStart { get; private set; }

    public int Calls { get; private set; }
}
