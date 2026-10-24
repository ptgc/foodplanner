using Foodplanner.Domain.Common;

namespace Foodplanner.Domain.Catalog;

/// <summary>
/// A measuring unit. Global only (no household): the seed defines them. <see cref="FactorToBase"/> is how many base
/// units one of this unit is; the base unit of Mass is g, of Volume ml and of Count stk (factor 1).
/// </summary>
public sealed class Unit : Entity, ICatalogNamed
{
    public const int MaxCodeLength = 20;
    public const int MaxNameLength = 50;

    private Unit()
    {
    }

    public Unit(string code, string name, UnitDimension dimension, decimal factorToBase)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaxCodeLength)
        {
            throw new ArgumentException("Invalid unit code.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            throw new ArgumentException("Invalid unit name.", nameof(name));
        }

        if (!Enum.IsDefined(dimension))
        {
            throw new ArgumentOutOfRangeException(nameof(dimension));
        }

        if (factorToBase <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factorToBase), "The factor must be positive.");
        }

        Code = code;
        Name = name;
        Dimension = dimension;
        FactorToBase = factorToBase;
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public UnitDimension Dimension { get; private set; }

    public decimal FactorToBase { get; private set; }
}
