using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Localization;

namespace Foodplanner.Domain.Recipes;

/// <summary>
/// THE data file for the global categories and tags (T-023). Name is the English name (the row stores it; the last-resort
/// fallback) and Nb the Norwegian bokmål name (Q-013, T-039). Key is the stable ASCII identity and gives the fixed id (<see cref="SeedIds"/>);
/// never change a key. A changed name reaches existing databases only through a new migration (the seed is migration data).
/// </summary>
public static class TagSeed
{
    public sealed record Row(string Key, string Name, string Nb, TagKind Kind)
    {
        public IReadOnlyDictionary<string, string> Names => new Dictionary<string, string> { [Locales.English] = Name, [Locales.Norwegian] = Nb };

        public Guid Id => SeedIds.For("tag", Key);
    }

    public static IReadOnlyList<Row> All { get; } =
    [
        new("middag", "Dinner", "Middag", TagKind.Category),
        new("lunsj", "Lunch", "Lunsj", TagKind.Category),
        new("frokost", "Breakfast", "Frokost", TagKind.Category),
        new("dessert", "Dessert", "Dessert", TagKind.Category),
        new("bakst", "Baking", "Bakst", TagKind.Category),

        new("vegetar", "Vegetarian", "Vegetar", TagKind.Tag),
        new("fisk", "Fish", "Fisk", TagKind.Tag),
        new("kylling", "Chicken", "Kylling", TagKind.Tag),
        new("kjott", "Meat", "Kjøtt", TagKind.Tag),
        new("pasta", "Pasta", "Pasta", TagKind.Tag),
        new("suppe", "Soup", "Suppe", TagKind.Tag),
        new("rask", "Quick", "Rask", TagKind.Tag), // under 30 minutes, by convention
        new("barnevennlig", "Kid friendly", "Barnevennlig", TagKind.Tag),
    ];
}
