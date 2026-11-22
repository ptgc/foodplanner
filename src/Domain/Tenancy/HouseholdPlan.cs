using Foodplanner.Domain.Common;

namespace Foodplanner.Domain.Tenancy;

/// <summary>
/// One limit of a household plan (T-123): unlimited, or a count from 0 to <see cref="MaxCount"/> (0 means none allowed).
/// The default value is unlimited. Pure value type; the limiter turns it into the number its conditional UPDATE compares with.
/// </summary>
public readonly record struct AiLimit
{
    public const int MaxCount = 10_000;

    private AiLimit(int? count) => Count = count;

    /// <summary>The count, or <c>null</c> for unlimited.</summary>
    public int? Count { get; }

    public bool IsUnlimited => Count is null;

    public static AiLimit Unlimited => new(null);

    /// <summary>A checked count for a plan a super admin sets: 0 to <see cref="MaxCount"/>.</summary>
    public static AiLimit Of(int count) =>
        IsValidCount(count) ? new(count) : throw new ArgumentOutOfRangeException(nameof(count), count, $"A limit is 0 to {MaxCount}.");

    /// <summary>A limit from configuration (the default plan): not range checked, and a value at or below 0 means nothing allowed.</summary>
    public static AiLimit Configured(int count) => new(Math.Max(0, count));

    public static bool IsValidCount(int count) => count is >= 0 and <= MaxCount;

    /// <summary>The number a conditional UPDATE compares the counter with: unlimited never fails the comparison.</summary>
    public int ToCeiling() => Count ?? int.MaxValue;

    /// <summary>True when the limit refuses everything (0): no database write is needed to know.</summary>
    public bool IsNone => Count == 0;
}

/// <summary>The three limits of a plan: photo imports per day, line help per day and per month (UTC periods).</summary>
public readonly record struct HouseholdPlanLimits(AiLimit PhotoImportsPerDay, AiLimit LineHelpPerDay, AiLimit LineHelpPerMonth)
{
    /// <summary>The plan's limits when the household has one, the configured defaults otherwise.</summary>
    public static HouseholdPlanLimits Resolve(HouseholdPlan? plan, HouseholdPlanLimits defaults) => plan?.Limits ?? defaults;
}

/// <summary>
/// A household's plan (T-123, "abonnement", Q-038 option 1): its Claude limits, set by a super admin in a support write session.
/// No row means "the default plan", read live from configuration. Stored as nullable counts (null = unlimited). The label is
/// free text for the admin only. HouseholdId is assigned by Infrastructure.
/// </summary>
public sealed class HouseholdPlan : Entity, ITenantEntity
{
    public const int MaxNameLength = 40;

    private HouseholdPlan()
    {
    }

    public HouseholdPlan(string? name, HouseholdPlanLimits limits) => Replace(name, limits);

    public Guid HouseholdId { get; private set; }

    public string? Name { get; private set; }

    public int? PhotoImportsPerDay { get; private set; }

    public int? LineHelpPerDay { get; private set; }

    public int? LineHelpPerMonth { get; private set; }

    public HouseholdPlanLimits Limits => new(FromStored(PhotoImportsPerDay), FromStored(LineHelpPerDay), FromStored(LineHelpPerMonth));

    /// <summary>Replaces the whole plan. Throws when the name is longer than 40 characters or holds a control character.</summary>
    public void Replace(string? name, HouseholdPlanLimits limits)
    {
        if (!TryNormalizeName(name, out var normalized))
        {
            throw new ArgumentException($"A plan name is at most {MaxNameLength} characters with no control characters.", nameof(name));
        }

        Name = normalized;
        PhotoImportsPerDay = limits.PhotoImportsPerDay.Count;
        LineHelpPerDay = limits.LineHelpPerDay.Count;
        LineHelpPerMonth = limits.LineHelpPerMonth.Count;
    }

    /// <summary>Trims; an empty name becomes null. False when too long or holding a control character.</summary>
    public static bool TryNormalizeName(string? name, out string? normalized)
    {
        normalized = null;
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return true;
        }

        if (trimmed.Length > MaxNameLength || trimmed.Any(char.IsControl))
        {
            return false;
        }

        normalized = trimmed;
        return true;
    }

    private static AiLimit FromStored(int? count) => count is { } n ? AiLimit.Configured(n) : AiLimit.Unlimited;
}
