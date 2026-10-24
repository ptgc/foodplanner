using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Catalog;

/// <summary>
/// The name of one global catalog row (ingredient, unit or tag) in one locale (T-039, Q-013). Global data written only
/// by the seed (migration data), never at request time, and never for a household's own rows: those keep the single
/// name the household typed. A locale is data (the <see cref="Locale"/> code), so adding one needs no schema change.
/// The row's own <c>Name</c> column is the English name and the last fallback.
/// </summary>
public sealed class CatalogTranslation
{
    public const int MaxKindLength = 20;
    public const int MaxLocaleLength = 10;
    public const int MaxNameLength = 100;

    /// <summary>What a row translates; the same strings as the seed id kinds (<see cref="SeedIds"/>).</summary>
    public const string KindIngredient = "ingredient";

    public const string KindUnit = "unit";
    public const string KindTag = "tag";

    private CatalogTranslation()
    {
    }

    public string Kind { get; private set; } = string.Empty;

    public Guid EntityId { get; private set; }

    public string Locale { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Every translation of every seed row in a stable order: the data for <c>HasData</c>.</summary>
    public static IEnumerable<(string Kind, Guid EntityId, string Locale, string Name)> SeedRows()
    {
        foreach (var i in IngredientSeed.All)
        {
            foreach (var (locale, name) in i.Names)
            {
                yield return (KindIngredient, i.Id, locale, name);
            }
        }

        foreach (var u in UnitSeed.All)
        {
            foreach (var (locale, name) in u.Names)
            {
                yield return (KindUnit, u.Id, locale, name);
            }
        }

        foreach (var t in TagSeed.All)
        {
            foreach (var (locale, name) in t.Names)
            {
                yield return (KindTag, t.Id, locale, name);
            }
        }
    }
}
