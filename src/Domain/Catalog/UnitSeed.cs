namespace Foodplanner.Domain.Catalog;

/// <summary>
/// The seeded units. Base units: g (Mass), ml (Volume), stk (Count).
/// Approximations, by convention in Norwegian recipes: ts (teskje) = 5 ml, ss (spiseskje) = 15 ml, kopp = 250 ml
/// (2,5 dl; not the 237 ml US cup). They are volumes, so 1 ss of flour is 15 ml, not a mass: converting to grams needs a
/// density that the model does not hold. fedd (clove) and klype (pinch) are Count units with factor 1 only as a
/// reference; they do not convert to stk or each other (see <see cref="UnitConversion"/>).
/// </summary>
public static class UnitSeed
{
    public sealed record Row(string Code, string Name, UnitDimension Dimension, decimal FactorToBase)
    {
        public Guid Id => SeedIds.For("unit", Code);

        public Unit ToUnit() => new(Code, Name, Dimension, FactorToBase);
    }

    public static IReadOnlyList<Row> All { get; } =
    [
        new("g", "gram", UnitDimension.Mass, 1m),
        new("kg", "kilogram", UnitDimension.Mass, 1000m),
        new("ml", "milliliter", UnitDimension.Volume, 1m),
        new("dl", "desiliter", UnitDimension.Volume, 100m),
        new("l", "liter", UnitDimension.Volume, 1000m),
        new("ts", "teskje", UnitDimension.Volume, 5m),
        new("ss", "spiseskje", UnitDimension.Volume, 15m),
        new("kopp", "kopp", UnitDimension.Volume, 250m),
        new("stk", "stykk", UnitDimension.Count, 1m),
        new("fedd", "fedd", UnitDimension.Count, 1m),
        new("klype", "klype", UnitDimension.Count, 1m),
    ];
}
