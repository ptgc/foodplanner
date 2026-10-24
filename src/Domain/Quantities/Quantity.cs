using Foodplanner.Domain.Catalog;

namespace Foodplanner.Domain.Quantities;

/// <summary>
/// An amount of something in a unit. <see cref="Amount"/> is null for "to taste" (salt, pepper): the line exists but has
/// no number, and it is never added to anything. A real amount is never negative. Decimal throughout, never floating point.
/// </summary>
public sealed record Quantity
{
    public Quantity(decimal? amount, Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "An amount cannot be negative.");
        }

        Amount = amount;
        Unit = unit;
    }

    public decimal? Amount { get; }

    public Unit Unit { get; }

    public bool IsToTaste => Amount is null;

    /// <summary>The amount in the base unit of its dimension (g, ml, stk), or null for to-taste.</summary>
    public decimal? BaseAmount => Amount * Unit.FactorToBase;

    /// <summary>Same quantity in <paramref name="target"/>, or null when the units are not convertible (see <see cref="UnitConversion"/>).</summary>
    public Quantity? ConvertTo(Unit target)
    {
        if (!UnitConversion.CanConvert(Unit, target))
        {
            return null;
        }

        return new Quantity(Amount is null ? null : UnitConversion.Convert(Amount.Value, Unit, target), target);
    }

    // Value equality on what the unit is (its code), not on the Unit instance, so equal quantities compare equal
    // whether the unit came from the database or from the seed.
    public bool Equals(Quantity? other) =>
        other is not null && Amount == other.Amount && Unit.Code == other.Unit.Code;

    public override int GetHashCode() => HashCode.Combine(Amount, Unit.Code);

    public override string ToString() => Amount is null ? $"{Unit.Code} (to taste)" : $"{Amount:0.####} {Unit.Code}";
}
