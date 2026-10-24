using Foodplanner.Domain.Catalog;

namespace Foodplanner.Domain.Quantities;

/// <summary>
/// The units merging may pick as a display unit, looked up by code. Callers pass the units loaded from the database so
/// results carry real unit rows; <see cref="Seed"/> is the seed data for tests and for code with no database.
/// </summary>
public sealed class UnitCatalog
{
    private readonly Dictionary<string, Unit> _byCode;

    public UnitCatalog(IEnumerable<Unit> units)
    {
        _byCode = units.ToDictionary(u => u.Code, StringComparer.Ordinal);
    }

    public static UnitCatalog Seed { get; } = new(UnitSeed.All.Select(r => r.ToUnit()));

    public Unit Get(string code) =>
        _byCode.TryGetValue(code, out var unit)
            ? unit
            : throw new InvalidOperationException($"The unit catalog has no unit '{code}'.");
}
