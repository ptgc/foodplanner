using Foodplanner.Domain.Common;

namespace Foodplanner.Domain.Tenancy;

/// <summary>The tenant root. Has no HouseholdId of its own and is not an <see cref="ITenantEntity"/>.</summary>
public sealed class Household : Entity
{
    public const int MaxNameLength = 100;

    private Household()
    {
    }

    public Household(string name)
    {
        Name = NormalizeName(name) ?? throw new ArgumentException("Invalid household name.", nameof(name));
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Trims the name; returns null when it is empty or longer than <see cref="MaxNameLength"/>.</summary>
    public static string? NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength ? null : trimmed;
    }
}
