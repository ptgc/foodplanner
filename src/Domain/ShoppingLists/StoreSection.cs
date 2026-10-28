using System.Text.Json.Serialization;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// The part of a store an ingredient is found in (T-085, Q-019). A property of the ingredient; not the same as
/// <see cref="ShoppingSection"/> (the "to buy" or "check you have" part of a list). The numeric values are stable
/// identities and say nothing about order: the walking order is <see cref="StoreSectionOrder"/>, so a custom order per
/// store (T-087) can replace it without touching the data.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StoreSection
{
    /// <summary>Anything without a known section: manual items and own ingredients not yet assigned.</summary>
    Other = 0,

    /// <summary>Frukt og grønt, including fresh herbs.</summary>
    FruitAndVegetables = 1,

    /// <summary>Kjøtt, fisk og ost: the chilled counter.</summary>
    MeatFishAndCheese = 2,

    /// <summary>Frysevarer.</summary>
    Frozen = 3,

    /// <summary>Meieri og juice: the chilled shelf (milk, juice, sour cream, yoghurt, butter, eggs).</summary>
    Dairy = 4,

    /// <summary>Tørrvarer: pasta, rice, flour, canned goods, spices, oils, bread and baking.</summary>
    DryGoods = 5,
}

public static class StoreSections
{
    /// <summary>Strict parse: exact names only (case-insensitive), no numbers, no whitespace.</summary>
    public static bool TryParse(string? value, out StoreSection section)
    {
        foreach (var candidate in Enum.GetValues<StoreSection>())
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                section = candidate;
                return true;
            }
        }

        section = default;
        return false;
    }
}
