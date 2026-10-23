using System.Text.Json.Serialization;

namespace Foodplanner.Domain.Tenancy;

/// <summary>A member's role in one household (per membership). Roles are hierarchical: Owner &gt; Adult &gt; Child.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HouseholdRole
{
    // The numeric order is the hierarchy used by Satisfies. Roles are stored as strings.
    Child = 1,
    Adult = 2,
    Owner = 3,
}

public static class HouseholdRoles
{
    /// <summary>True when <paramref name="actual"/> may do everything <paramref name="required"/> may.</summary>
    public static bool Satisfies(this HouseholdRole actual, HouseholdRole required) =>
        Enum.IsDefined(actual) && Enum.IsDefined(required) && actual >= required;

    /// <summary>Roles an invite may grant (Q-005): never Owner.</summary>
    public static bool IsInvitable(this HouseholdRole role) => role is HouseholdRole.Adult or HouseholdRole.Child;

    /// <summary>Strict parse of a role name: exact names only (case-insensitive), no numbers, no whitespace.</summary>
    public static bool TryParse(string? value, out HouseholdRole role)
    {
        foreach (var candidate in Enum.GetValues<HouseholdRole>())
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                role = candidate;
                return true;
            }
        }

        role = default;
        return false;
    }
}
