using System.Text.Json.Serialization;

namespace Foodplanner.Domain.Catalog;

/// <summary>What a unit measures. Units convert only within a dimension.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UnitDimension
{
    Mass = 1,
    Volume = 2,
    Count = 3,
}

public static class UnitDimensions
{
    /// <summary>Strict parse: exact names only (case-insensitive), no numbers, no whitespace.</summary>
    public static bool TryParse(string? value, out UnitDimension dimension)
    {
        foreach (var candidate in Enum.GetValues<UnitDimension>())
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                dimension = candidate;
                return true;
            }
        }

        dimension = default;
        return false;
    }
}
