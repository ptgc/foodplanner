using System.Text.Json.Serialization;

namespace Foodplanner.Domain.Recipes;

/// <summary>Category: what kind of meal (Middag, Dessert). Tag: a property of the recipe (Vegetar, Rask).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TagKind
{
    Category = 1,
    Tag = 2,
}

public static class TagKinds
{
    /// <summary>Strict parse: exact names only (case-insensitive), no numbers.</summary>
    public static bool TryParse(string? value, out TagKind kind)
    {
        foreach (var candidate in Enum.GetValues<TagKind>())
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}
