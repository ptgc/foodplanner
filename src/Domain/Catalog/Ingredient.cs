using Foodplanner.Domain.Common;
using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Catalog;

/// <summary>
/// An ingredient: a global catalog row (<see cref="HouseholdId"/> null, from the seed) or one a household created.
/// A staple (salt, oil, flour) is never auto-added to a shopping list. <see cref="Dimension"/> is what the ingredient is
/// normally measured in, a hint for the recipe editor (T-021).
/// </summary>
public sealed class Ingredient : Entity, ICatalogEntity, ICatalogNamed
{
    public const int MaxNameLength = 100;

    private Ingredient()
    {
    }

    /// <summary>A household's own ingredient. Pass <see cref="Guid.Empty"/> to let Infrastructure assign the current household.</summary>
    public Ingredient(Guid householdId, string name, bool isStaple, UnitDimension dimension, StoreSection section = StoreSection.Other)
    {
        if (!Enum.IsDefined(section))
        {
            throw new ArgumentOutOfRangeException(nameof(section));
        }

        if (!Enum.IsDefined(dimension))
        {
            throw new ArgumentOutOfRangeException(nameof(dimension));
        }

        HouseholdId = householdId;
        Name = NormalizeName(name) ?? throw new ArgumentException("Invalid ingredient name.", nameof(name));
        IsStaple = isStaple;
        Dimension = dimension;
        Section = section;
    }

    public Guid? HouseholdId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public bool IsStaple { get; private set; }

    public UnitDimension Dimension { get; private set; }

    /// <summary>The store section the ingredient is found in (T-085). Own ingredients default to Other; the seed sets it for every global row.</summary>
    public StoreSection Section { get; private set; }

    public void ChangeSection(StoreSection section)
    {
        if (!Enum.IsDefined(section))
        {
            throw new ArgumentOutOfRangeException(nameof(section));
        }

        Section = section;
    }

    public void Rename(string name) =>
        Name = NormalizeName(name) ?? throw new ArgumentException("Invalid ingredient name.", nameof(name));

    /// <summary>Trims and collapses inner whitespace; null when empty, too long or containing control characters.</summary>
    public static string? NormalizeName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var collapsed = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length is 0 or > MaxNameLength || collapsed.Any(char.IsControl) ? null : collapsed;
    }
}
