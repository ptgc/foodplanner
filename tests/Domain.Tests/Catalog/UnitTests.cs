using Foodplanner.Domain.Catalog;

namespace Foodplanner.Domain.Tests.Catalog;

public class UnitTests
{
    private static Unit U(string code) => UnitSeed.All.Single(r => r.Code == code).ToUnit();

    [Fact]
    public void Seed_has_the_required_units_and_each_dimension()
    {
        string[] required = ["g", "kg", "ml", "dl", "l", "ts", "ss", "kopp", "stk", "fedd", "klype"];

        Assert.Equal(required.Order(), UnitSeed.All.Select(r => r.Code).Order());
        Assert.Equal(UnitSeed.All.Count, UnitSeed.All.Select(r => r.Code).Distinct().Count());
        Assert.Equal(UnitSeed.All.Count, UnitSeed.All.Select(r => r.Id).Distinct().Count());
        Assert.All(UnitSeed.All, r => Assert.True(r.FactorToBase > 0));
        Assert.Equal([UnitDimension.Mass, UnitDimension.Volume, UnitDimension.Count], UnitSeed.All.Select(r => r.Dimension).Distinct().Order());
    }

    [Fact]
    public void Base_units_have_factor_one()
    {
        Assert.Equal(1m, U("g").FactorToBase);
        Assert.Equal(1m, U("ml").FactorToBase);
        Assert.Equal(1m, U("stk").FactorToBase);
    }

    [Theory]
    [InlineData("kg", 1000)]
    [InlineData("dl", 100)]
    [InlineData("l", 1000)]
    [InlineData("ts", 5)]
    [InlineData("ss", 15)]
    [InlineData("kopp", 250)]
    public void Factors_to_the_base_unit(string code, decimal factor) => Assert.Equal(factor, U(code).FactorToBase);

    [Theory]
    [InlineData(1, "kg", "g", 1000)]
    [InlineData(500, "g", "kg", 0.5)]
    [InlineData(1, "l", "dl", 10)]
    [InlineData(2.5, "dl", "ml", 250)]
    [InlineData(1, "ss", "ts", 3)]
    [InlineData(1, "kopp", "dl", 2.5)]
    [InlineData(3, "dl", "ss", 20)]
    [InlineData(4, "ts", "ml", 20)]
    [InlineData(2, "stk", "stk", 2)]
    [InlineData(3, "fedd", "fedd", 3)]
    public void Converts_within_a_dimension(decimal quantity, string from, string to, decimal expected) =>
        Assert.Equal(expected, UnitConversion.Convert(quantity, U(from), U(to)));

    [Theory]
    [InlineData("g", "ml")]
    [InlineData("kg", "l")]
    [InlineData("ss", "g")]
    [InlineData("stk", "g")]
    [InlineData("dl", "stk")]
    public void Does_not_convert_across_dimensions(string from, string to)
    {
        Assert.False(UnitConversion.CanConvert(U(from), U(to)));
        Assert.Null(UnitConversion.Convert(1, U(from), U(to)));
        Assert.Null(UnitConversion.Convert(1, U(to), U(from)));
    }

    [Theory]
    [InlineData("stk", "fedd")]
    [InlineData("fedd", "klype")]
    [InlineData("klype", "stk")]
    public void Count_units_are_not_interchangeable(string from, string to) =>
        Assert.Null(UnitConversion.Convert(1, U(from), U(to)));

    [Fact]
    public void Round_trip_through_every_convertible_pair_is_exact()
    {
        var units = UnitSeed.All.Select(r => r.ToUnit()).ToList();
        var pairs = 0;
        foreach (var from in units)
        {
            foreach (var to in units.Where(u => UnitConversion.CanConvert(from, u)))
            {
                var there = UnitConversion.Convert(7m, from, to)!.Value;
                Assert.InRange(UnitConversion.Convert(there, to, from)!.Value, 6.999999999m, 7.000000001m); // decimal division is not exact for 1/15 and the like
                pairs++;
            }
        }

        Assert.True(pairs > 20); // not vacuous
    }

    [Fact]
    public void Unit_rejects_bad_data()
    {
        Assert.Throws<ArgumentException>(() => new Unit("", "x", UnitDimension.Mass, 1));
        Assert.Throws<ArgumentException>(() => new Unit("x", " ", UnitDimension.Mass, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Unit("x", "x", (UnitDimension)99, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Unit("x", "x", UnitDimension.Mass, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Unit("x", "x", UnitDimension.Mass, -1));
    }
}
