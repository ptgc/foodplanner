using System.Text.Json.Serialization;

namespace Foodplanner.Domain.Catalog;

/// <summary>
/// What an ingredient is, as far as the food type of a recipe goes (T-089, Q-021). The seed sets it for every global
/// ingredient; a household's own ingredient starts as <see cref="Unknown"/> until an adult sets it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProteinClass
{
    /// <summary>Not classified (the default of an own ingredient). Never decides a type on its own.</summary>
    Unknown = 0,

    /// <summary>No animal product: vegetables, fruit, grains, plant oils, spices ...</summary>
    None,

    /// <summary>Dairy, eggs, honey: animal products that are not meat or fish (vegetarian, not vegan).</summary>
    AnimalProduct,

    Fish,
    Seafood,
    Chicken,
    Beef,
    Pork,
    Lamb,

    /// <summary>Meat of unspecified or mixed kind (mince, sausages).</summary>
    Meat,
}

public static class ProteinClasses
{
    /// <summary>Strict parse: exact names only (case-insensitive), no numbers.</summary>
    public static bool TryParse(string? value, out ProteinClass proteinClass)
    {
        foreach (var candidate in Enum.GetValues<ProteinClass>())
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                proteinClass = candidate;
                return true;
            }
        }

        proteinClass = default;
        return false;
    }
}
