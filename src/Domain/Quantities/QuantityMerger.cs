using Foodplanner.Domain.Catalog;

namespace Foodplanner.Domain.Quantities;

/// <summary>
/// Merging quantities of ONE ingredient into the fewest lines (the caller groups by ingredient).
///
/// Two stages, so that merging is exact and therefore associative and commutative:
///  1. <see cref="Merge"/> adds exactly (decimal, no rounding) and picks the unit from the exact total.
///  2. <see cref="ForDisplay"/> rounds and re-picks the unit for a person to read. Never merge rounded results.
///
/// Rules:
///  - Mass units (g, kg) add to one Mass line; Volume units (ml, dl, l, ts, ss, kopp) add to one Volume line.
///  - Mass and Volume are NEVER merged (that needs a density per ingredient, which the model does not hold).
///  - Count units merge only with the SAME unit: stk with stk, fedd with fedd, klype with klype.
///  - "To taste" (null amount) lines are never added; one line is kept per distinct unit (duplicates collapse).
///  - Output order is fixed: Mass, Volume, Count by unit code, then to-taste by unit code.
///
/// Display unit (<see cref="Merge"/> and <see cref="ForDisplay"/> use the same rule, on the exact respectively rounded
/// base amount): Mass: below 1000 g in g, from 1000 g in kg. Volume: below 1000 ml in ml, from 1000 ml in l
/// (dl, ts, ss, kopp are input units only, never chosen). Count keeps its own unit.
///
/// Rounding (<see cref="ForDisplay"/>, <see cref="Round"/>), half away from zero, never to zero for a positive amount
/// (the minimum shown is one step): g 1, kg 0.1, ml 1, dl 0.1, l 0.1, ts 0.5, ss 0.5, kopp 0.25, stk 0.5, fedd 1, klype 1.
/// Mass and Volume are rounded in g / ml first and the unit is chosen after, so 999.6 g shows as 1 kg, not 1000 g.
/// </summary>
public static class QuantityMerger
{
    private static readonly Dictionary<string, decimal> Steps = new(StringComparer.Ordinal)
    {
        ["g"] = 1m,
        ["kg"] = 0.1m,
        ["ml"] = 1m,
        ["dl"] = 0.1m,
        ["l"] = 0.1m,
        ["ts"] = 0.5m,
        ["ss"] = 0.5m,
        ["kopp"] = 0.25m,
        ["stk"] = 0.5m,
        ["fedd"] = 1m,
        ["klype"] = 1m,
    };

    /// <summary>The rounding step of a unit. A unit outside the seed rounds to 0.1.</summary>
    public static decimal StepOf(Unit unit) => Steps.GetValueOrDefault(unit.Code, 0.1m);

    /// <summary>Exact merge, see the class remarks. Order of the input does not matter.</summary>
    public static IReadOnlyList<Quantity> Merge(IEnumerable<Quantity> quantities, UnitCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(quantities);
        ArgumentNullException.ThrowIfNull(catalog);

        decimal mass = 0, volume = 0;
        var hasMass = false;
        var hasVolume = false;
        var counts = new SortedDictionary<string, (Unit Unit, decimal Total)>(StringComparer.Ordinal);
        var toTaste = new SortedDictionary<string, Unit>(StringComparer.Ordinal);

        foreach (var q in quantities)
        {
            if (q.Amount is null)
            {
                toTaste.TryAdd(q.Unit.Code, q.Unit);
                continue;
            }

            switch (q.Unit.Dimension)
            {
                case UnitDimension.Mass:
                    mass += q.Amount.Value * q.Unit.FactorToBase;
                    hasMass = true;
                    break;
                case UnitDimension.Volume:
                    volume += q.Amount.Value * q.Unit.FactorToBase;
                    hasVolume = true;
                    break;
                default:
                    // Count units are not interchangeable: sum as given, per unit code.
                    counts[q.Unit.Code] = (q.Unit, counts.GetValueOrDefault(q.Unit.Code).Total + q.Amount.Value);
                    break;
            }
        }

        var result = new List<Quantity>();
        if (hasMass)
        {
            result.Add(InDisplayUnit(mass, UnitDimension.Mass, catalog));
        }

        if (hasVolume)
        {
            result.Add(InDisplayUnit(volume, UnitDimension.Volume, catalog));
        }

        result.AddRange(counts.Values.Select(c => new Quantity(c.Total, c.Unit)));
        result.AddRange(toTaste.Values.Select(u => new Quantity(null, u)));
        return result;
    }

    /// <summary>Rounds one quantity to its unit's step; see the class remarks. Does not change the unit.</summary>
    public static Quantity Round(Quantity quantity)
    {
        if (quantity.Amount is not { } amount)
        {
            return quantity;
        }

        return new Quantity(RoundToStep(amount, StepOf(quantity.Unit)), quantity.Unit);
    }

    /// <summary>
    /// Makes merged lines readable: Mass and Volume are rounded in g / ml, the display unit is chosen from that, then
    /// rounded to the unit's step; Count is rounded to its step; to-taste is unchanged. Not for further merging.
    /// </summary>
    public static IReadOnlyList<Quantity> ForDisplay(IEnumerable<Quantity> quantities, UnitCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(quantities);
        ArgumentNullException.ThrowIfNull(catalog);
        return [.. quantities.Select(q => DisplayOne(q, catalog))];
    }

    /// <summary>Merge, then round for display.</summary>
    public static IReadOnlyList<Quantity> MergeForDisplay(IEnumerable<Quantity> quantities, UnitCatalog catalog) =>
        ForDisplay(Merge(quantities, catalog), catalog);

    private static Quantity DisplayOne(Quantity q, UnitCatalog catalog)
    {
        if (q.Amount is null || q.Unit.Dimension == UnitDimension.Count)
        {
            return Round(q);
        }

        var baseUnit = catalog.Get(q.Unit.Dimension == UnitDimension.Mass ? "g" : "ml");
        var baseRounded = RoundToStep(q.Amount.Value * q.Unit.FactorToBase, StepOf(baseUnit));
        return Round(InDisplayUnit(baseRounded, q.Unit.Dimension, catalog));
    }

    private static Quantity InDisplayUnit(decimal baseAmount, UnitDimension dimension, UnitCatalog catalog)
    {
        var (small, large) = dimension == UnitDimension.Mass ? ("g", "kg") : ("ml", "l");
        var unit = catalog.Get(baseAmount >= 1000m ? large : small);
        return new Quantity(baseAmount / unit.FactorToBase, unit);
    }

    private static decimal RoundToStep(decimal amount, decimal step)
    {
        if (amount == 0)
        {
            return 0;
        }

        var rounded = Math.Round(amount / step, 0, MidpointRounding.AwayFromZero) * step;
        return rounded == 0 ? step : rounded;
    }
}
