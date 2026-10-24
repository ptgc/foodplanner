using Foodplanner.Domain.Catalog;

namespace Foodplanner.Domain.Recipes;

/// <summary>
/// THE data file for the global categories and tags (T-023). Names are Norwegian bokmål (Q-013 is open: to change or
/// translate, edit only the Name column). Key is the stable ASCII identity and gives the fixed id (<see cref="SeedIds"/>);
/// never change a key. A changed name reaches existing databases only through a new migration (the seed is migration data).
/// </summary>
public static class TagSeed
{
    public sealed record Row(string Key, string Name, TagKind Kind)
    {
        public Guid Id => SeedIds.For("tag", Key);
    }

    public static IReadOnlyList<Row> All { get; } =
    [
        new("middag", "Middag", TagKind.Category),
        new("lunsj", "Lunsj", TagKind.Category),
        new("frokost", "Frokost", TagKind.Category),
        new("dessert", "Dessert", TagKind.Category),
        new("bakst", "Bakst", TagKind.Category),

        new("vegetar", "Vegetar", TagKind.Tag),
        new("fisk", "Fisk", TagKind.Tag),
        new("kylling", "Kylling", TagKind.Tag),
        new("kjott", "Kjøtt", TagKind.Tag),
        new("pasta", "Pasta", TagKind.Tag),
        new("suppe", "Suppe", TagKind.Tag),
        new("rask", "Rask", TagKind.Tag), // under 30 minutes, by convention
        new("barnevennlig", "Barnevennlig", TagKind.Tag),
    ];
}
