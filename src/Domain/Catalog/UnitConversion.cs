namespace Foodplanner.Domain.Catalog;

/// <summary>
/// Conversion between units. Only what is needed to make the unit data testable; merging and scaling are T-031.
/// Mass and Volume units convert within their dimension via the base unit (g, ml). Count units (stk, fedd, klype)
/// are NOT interchangeable: a clove is not a piece, so a Count unit converts only to itself. Mass never converts to
/// Volume (that needs a density per ingredient, which the model does not have).
/// </summary>
public static class UnitConversion
{
    public static bool CanConvert(Unit from, Unit to) =>
        from.Dimension == to.Dimension && (from.Dimension != UnitDimension.Count || from.Code == to.Code);

    /// <summary>Converts <paramref name="quantity"/> between units, or returns null when they are not convertible.</summary>
    public static decimal? Convert(decimal quantity, Unit from, Unit to) =>
        CanConvert(from, to) ? quantity * from.FactorToBase / to.FactorToBase : null;
}
