using System.Text.Json.Serialization;
using Foodplanner.Domain.Catalog;

namespace Foodplanner.Domain.Recipes;

/// <summary>
/// The one food type of a recipe: its main protein (T-089, Q-021). It replaces the old food tags Vegetar, Fisk, Kylling and
/// Kjøtt. Derived from the ingredients unless an adult has chosen one by hand (<see cref="Recipe.FoodTypeIsManual"/>).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FoodType
{
    Unknown = 0,
    Fish,
    Seafood,
    Chicken,
    Beef,
    Pork,
    Lamb,
    MixedMeat,
    Vegetarian,
    Vegan,
}

public static class FoodTypes
{
    /// <summary>Strict parse: exact names only (case-insensitive), no numbers.</summary>
    public static bool TryParse(string? value, out FoodType type)
    {
        foreach (var candidate in Enum.GetValues<FoodType>())
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                type = candidate;
                return true;
            }
        }

        type = default;
        return false;
    }
}

/// <summary>An ingredient line as the deriver sees it: the protein class of its ingredient, its purpose and whether it is optional.</summary>
public readonly record struct FoodTypeLine(ProteinClass Protein, IngredientPurpose Purpose, bool IsOptional);

/// <summary>
/// Derives the food type of a recipe from its ingredient lines (pure, CLAUDE.md rule 3). Only Main-purpose, non-optional lines
/// count (frying fat, serving sides, garnish and seasoning do not decide the type, nor does anything the cook may leave out).
/// <list type="number">
/// <item>Meat beats everything: one kind of meat gives that type; two or more different specific kinds, or only unspecified meat,
/// give <see cref="FoodType.MixedMeat"/> (unspecified meat next to one specific kind is taken as that kind).</item>
/// <item>Else fish (fish beats seafood), else seafood.</item>
/// <item>Else, if a counted ingredient is unclassified, Unknown (it might be meat); else any animal product (dairy, egg) gives
/// Vegetarian, and none gives Vegan.</item>
/// <item>No counted line: Unknown.</item>
/// </list>
/// </summary>
public static class FoodTypeDeriver
{
    public static FoodType Derive(IEnumerable<FoodTypeLine> lines)
    {
        var counted = lines.Where(l => l.Purpose == IngredientPurpose.Main && !l.IsOptional).Select(l => l.Protein).ToList();
        if (counted.Count == 0)
        {
            return FoodType.Unknown;
        }

        var specificMeats = counted.Where(p => p is ProteinClass.Chicken or ProteinClass.Beef or ProteinClass.Pork or ProteinClass.Lamb).Distinct().ToList();
        if (specificMeats.Count > 1)
        {
            return FoodType.MixedMeat;
        }

        if (specificMeats.Count == 1)
        {
            return specificMeats[0] switch
            {
                ProteinClass.Chicken => FoodType.Chicken,
                ProteinClass.Beef => FoodType.Beef,
                ProteinClass.Pork => FoodType.Pork,
                _ => FoodType.Lamb,
            };
        }

        if (counted.Contains(ProteinClass.Meat))
        {
            return FoodType.MixedMeat;
        }

        if (counted.Contains(ProteinClass.Fish))
        {
            return FoodType.Fish;
        }

        if (counted.Contains(ProteinClass.Seafood))
        {
            return FoodType.Seafood;
        }

        if (counted.Contains(ProteinClass.Unknown))
        {
            return FoodType.Unknown;
        }

        return counted.Contains(ProteinClass.AnimalProduct) ? FoodType.Vegetarian : FoodType.Vegan;
    }
}
