using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Common;

namespace Foodplanner.Domain.Recipes;

/// <summary>
/// A category or tag for recipes: a global row (<see cref="HouseholdId"/> null, from the seed, read-only) or one a
/// household created (T-023). Same global-or-household pattern as <see cref="Ingredient"/>. Recipes link to tags through
/// <see cref="RecipeTag"/>.
/// </summary>
public sealed class Tag : Entity, ICatalogEntity, ICatalogNamed
{
    public const int MaxNameLength = 50;

    private Tag()
    {
    }

    /// <summary>A household's own tag. Pass <see cref="Guid.Empty"/> to let Infrastructure assign the current household.</summary>
    public Tag(Guid householdId, string name, TagKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        HouseholdId = householdId;
        Name = NormalizeName(name) ?? throw new ArgumentException("Invalid tag name.", nameof(name));
        Kind = kind;
    }

    public Guid? HouseholdId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public TagKind Kind { get; private set; }

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
