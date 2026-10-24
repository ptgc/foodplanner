using CsCheck;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Quantities;

namespace Foodplanner.Domain.Tests.Quantities;

public class QuantityMergerTests
{
    private static readonly UnitCatalog Catalog = UnitCatalog.Seed;

    private static Quantity Q(decimal? amount, string unit) => new(amount, Catalog.Get(unit));

    private static string Show(IEnumerable<Quantity> quantities) => string.Join("; ", quantities);

    private static IReadOnlyList<Quantity> Merge(params Quantity[] quantities) => QuantityMerger.Merge(quantities, Catalog);

    private static IReadOnlyList<Quantity> Display(params Quantity[] quantities) =>
        QuantityMerger.MergeForDisplay(quantities, Catalog);

    // ---- examples ----

    [Fact]
    public void Grams_and_kilograms_merge()
    {
        Assert.Equal([Q(500m, "g")], Display(Q(200m, "g"), Q(0.3m, "kg")));
        Assert.Equal([Q(500m, "g")], Merge(Q(200m, "g"), Q(0.3m, "kg")));
    }

    [Fact]
    public void Volumes_merge_into_ml_below_a_liter()
    {
        Assert.Equal([Q(500m, "ml")], Display(Q(2m, "dl"), Q(300m, "ml")));
    }

    [Fact]
    public void Spoons_merge_as_volume()
    {
        Assert.Equal([Q(20m, "ml")], Display(Q(1m, "ss"), Q(1m, "ts")));
    }

    [Fact]
    public void Mass_and_volume_are_never_merged()
    {
        var merged = Display(Q(200m, "g"), Q(2m, "dl"));

        Assert.Equal([Q(200m, "g"), Q(200m, "ml")], merged);
        Assert.Equal(Merge(Q(200m, "g"), Q(2m, "dl")), Merge(Q(2m, "dl"), Q(200m, "g")));
    }

    [Fact]
    public void Different_count_units_are_not_merged_and_equal_ones_are()
    {
        Assert.Equal([Q(2m, "fedd"), Q(3m, "stk")], Display(Q(1m, "stk"), Q(2m, "fedd"), Q(2m, "stk")));
        Assert.Equal([Q(3m, "fedd")], Display(Q(1m, "fedd"), Q(2m, "fedd")));
        Assert.Equal([Q(1m, "klype"), Q(1m, "stk")], Display(Q(1m, "stk"), Q(1m, "klype")));
    }

    [Fact]
    public void To_taste_stays_separate_and_never_adds()
    {
        var merged = Display(Q(null, "g"), Q(5m, "g"), Q(null, "ts"), Q(null, "g"));

        Assert.Equal([Q(5m, "g"), Q(null, "g"), Q(null, "ts")], merged);
        Assert.Equal([Q(null, "stk")], Display(Q(null, "stk")));
    }

    [Fact]
    public void Nothing_merges_to_nothing()
    {
        Assert.Empty(Merge());
    }

    [Fact]
    public void Merging_is_order_independent_for_a_mixed_list()
    {
        Quantity[] list =
        [
            Q(200m, "g"), Q(null, "ts"), Q(2m, "stk"), Q(1m, "ss"), Q(0.3m, "kg"), Q(2m, "fedd"), Q(3m, "dl"), Q(1m, "stk"),
        ];

        var expected = Merge(list);
        Assert.Equal([Q(500m, "g"), Q(315m, "ml"), Q(2m, "fedd"), Q(3m, "stk"), Q(null, "ts")], expected);
        Assert.Equal(expected, Merge([.. list.Reverse()]));
        Assert.Equal(expected, Merge([.. list.OrderBy(q => q.Unit.Code, StringComparer.Ordinal)]));
    }

    [Fact]
    public void Display_unit_switches_at_a_thousand()
    {
        Assert.Equal([Q(999m, "g")], Display(Q(999m, "g")));
        Assert.Equal([Q(1m, "kg")], Display(Q(1000m, "g")));
        Assert.Equal([Q(1.5m, "kg")], Display(Q(1m, "kg"), Q(500m, "g")));
        Assert.Equal([Q(999m, "ml")], Display(Q(9.99m, "dl")));
        Assert.Equal([Q(1m, "l")], Display(Q(10m, "dl")));
        Assert.Equal([Q(1.5m, "l")], Display(Q(1m, "l"), Q(2m, "kopp")));
        Assert.Equal([Q(1m, "l")], Display(Q(4m, "kopp")));
    }

    [Fact]
    public void Merge_without_rounding_keeps_exact_values()
    {
        // 1 ts + 1 ts + 1 ts = 15 ml, and 0.333 g stays 0.333 g until display.
        Assert.Equal([Q(15m, "ml")], Merge(Q(1m, "ts"), Q(1m, "ts"), Q(1m, "ts")));
        Assert.Equal([Q(0.333m, "g")], Merge(Q(0.333m, "g")));
    }

    // ---- rounding ----

    [Theory]
    [InlineData(250.4, "g", 250)]
    [InlineData(250.5, "g", 251)]
    [InlineData(0.04, "g", 1)] // never to zero
    [InlineData(0.04, "kg", 0.1)]
    [InlineData(1.25, "kg", 1.3)]
    [InlineData(1.24, "kg", 1.2)]
    [InlineData(0.74, "ts", 0.5)]
    [InlineData(0.75, "ts", 1)]
    [InlineData(1.25, "ss", 1.5)]
    [InlineData(1.2, "ss", 1)]
    [InlineData(0.12, "kopp", 0.25)]
    [InlineData(1.1, "kopp", 1)]
    [InlineData(1.125, "kopp", 1.25)]
    [InlineData(1.24, "stk", 1)]
    [InlineData(1.25, "stk", 1.5)]
    [InlineData(2.5, "fedd", 3)]
    [InlineData(2.4, "fedd", 2)]
    [InlineData(0, "g", 0)]
    public void Rounds_to_the_unit_step(double amount, string unit, double expected) =>
        Assert.Equal(Q((decimal)expected, unit), QuantityMerger.Round(Q((decimal)amount, unit)));

    [Fact]
    public void Rounding_leaves_to_taste_alone() =>
        Assert.Equal(Q(null, "g"), QuantityMerger.Round(Q(null, "g")));

    [Theory]
    [InlineData(999.4, "g", 999, "g")]
    [InlineData(999.5, "g", 1, "kg")] // rounds in grams first, so no "1000 g"
    [InlineData(1249, "g", 1.2, "kg")]
    [InlineData(1250, "g", 1.3, "kg")]
    [InlineData(999.5, "ml", 1, "l")]
    [InlineData(0.2, "ml", 1, "ml")]
    public void Display_rounds_in_the_base_unit_before_choosing_the_unit(double amount, string unit, double expected, string expectedUnit) =>
        Assert.Equal([Q((decimal)expected, expectedUnit)], QuantityMerger.ForDisplay([Q((decimal)amount, unit)], Catalog));

    [Fact]
    public void Display_rounds_count_units_and_leaves_to_taste()
    {
        Assert.Equal(
            [Q(1.5m, "stk"), Q(null, "g")],
            QuantityMerger.ForDisplay([Q(1.4m, "stk"), Q(null, "g")], Catalog));
    }

    // ---- conversion ----

    [Theory]
    [InlineData("g", "kg")]
    [InlineData("kg", "g")]
    [InlineData("ml", "dl")]
    [InlineData("l", "ss")]
    [InlineData("kopp", "ts")]
    [InlineData("stk", "stk")]
    public void Round_trip_conversion_is_exact(string a, string b)
    {
        var start = Q(7.25m, a);

        var there = start.ConvertTo(Catalog.Get(b));
        Assert.NotNull(there);
        Assert.Equal(start, there.ConvertTo(Catalog.Get(a)));
    }

    [Fact]
    public void Conversion_across_dimensions_or_between_count_units_is_null()
    {
        Assert.Null(Q(1m, "g").ConvertTo(Catalog.Get("ml")));
        Assert.Null(Q(1m, "stk").ConvertTo(Catalog.Get("fedd")));
        Assert.Equal(Q(null, "kg"), Q(null, "g").ConvertTo(Catalog.Get("kg")));
    }

    [Fact]
    public void A_negative_amount_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Q(-1m, "g"));

    [Fact]
    public void Value_equality_ignores_the_unit_instance()
    {
        var other = UnitSeed.All.Single(r => r.Code == "g").ToUnit();

        Assert.Equal(new Quantity(5m, other), Q(5m, "g"));
        Assert.NotEqual(Q(5m, "g"), Q(5m, "ml"));
    }

    // ---- properties ----

    private static readonly string[] Codes = [.. UnitSeed.All.Select(r => r.Code)];

    // Amounts with up to two decimals, so every sum is exact in decimal. Null (to taste) one time in six.
    private static readonly Gen<Quantity> AnyQuantity =
        Gen.Select(
            Gen.Int[0, 5],
            Gen.Int[0, 500_000],
            Gen.OneOf(Codes),
            (nullDie, hundredths, code) => new Quantity(nullDie == 0 ? null : hundredths / 100m, Catalog.Get(code)));

    private static readonly Gen<Quantity[]> AnyList = AnyQuantity.Array[0, 12];

    [Fact]
    public void Property_merge_is_commutative_for_any_order()
    {
        Gen.Select(AnyList, Gen.Int, (list, seed) => (list, seed)).Sample(
            t =>
            {
                var shuffled = t.list.ToArray();
                new Random(t.seed).Shuffle(shuffled);
                return Same(Merge(t.list), Merge(shuffled));
            },
            iter: 1000);
    }

    [Fact]
    public void Property_merge_is_associative()
    {
        // merge(merge(a, b) + c) == merge(a + merge(b, c)) == merge(a + b + c)
        Gen.Select(AnyList, AnyList, AnyList, (a, b, c) => (a, b, c)).Sample(
            t =>
            {
                var left = Merge([.. Merge([.. t.a, .. t.b]), .. t.c]);
                var right = Merge([.. t.a, .. Merge([.. t.b, .. t.c])]);
                var flat = Merge([.. t.a, .. t.b, .. t.c]);
                return Same(left, right) && Same(left, flat);
            },
            iter: 1000);
    }

    [Fact]
    public void Property_merge_is_idempotent()
    {
        AnyList.Sample(list =>
        {
            var once = Merge(list);
            return Same(once, Merge([.. once]));
        });
    }

    [Fact]
    public void Property_mass_volume_and_count_totals_are_preserved_and_never_mixed()
    {
        AnyList.Sample(list =>
        {
            var merged = Merge(list);
            return Total(list, UnitDimension.Mass) == Total(merged, UnitDimension.Mass)
                && Total(list, UnitDimension.Volume) == Total(merged, UnitDimension.Volume)
                && merged.Count(q => q.Unit.Dimension == UnitDimension.Mass && !q.IsToTaste) <= 1
                && merged.Count(q => q.Unit.Dimension == UnitDimension.Volume && !q.IsToTaste) <= 1
                && merged.Where(q => !q.IsToTaste && q.Unit.Dimension == UnitDimension.Count).Select(q => q.Unit.Code).Distinct().Count()
                    == merged.Count(q => !q.IsToTaste && q.Unit.Dimension == UnitDimension.Count);
        });
    }

    [Fact]
    public void Property_conversion_round_trips_exactly_within_mass_and_volume()
    {
        Gen.Select(Gen.Int[0, 500_000], Gen.OneOf("g", "kg", "ml", "dl", "l"), Gen.OneOf("g", "kg", "ml", "dl", "l"), (h, a, b) => (h, a, b))
            .Sample(t =>
            {
                var start = Q(t.h / 100m, t.a);
                var there = start.ConvertTo(Catalog.Get(t.b));
                return there is null
                    ? Catalog.Get(t.a).Dimension != Catalog.Get(t.b).Dimension
                    : start == there.ConvertTo(Catalog.Get(t.a));
            });
    }

    [Fact]
    public void Property_display_is_stable_and_within_one_step_of_the_exact_total()
    {
        AnyList.Sample(list =>
        {
            var exact = Merge(list);
            var shown = QuantityMerger.ForDisplay(exact, Catalog);
            if (shown.Count != exact.Count)
            {
                return false;
            }

            for (var i = 0; i < exact.Count; i++)
            {
                if (exact[i].Amount is not { } e || exact[i].Unit.Dimension == UnitDimension.Count)
                {
                    continue;
                }

                var s = shown[i];
                var step = QuantityMerger.StepOf(s.Unit);
                var diff = Math.Abs((s.Amount!.Value * s.Unit.FactorToBase) - (e * exact[i].Unit.FactorToBase));
                // within half a step of the shown unit, plus half a base unit from the first rounding (or the minimum step)
                if (diff > (step * s.Unit.FactorToBase / 2m) + 0.5m && s.Amount != step)
                {
                    return false;
                }
            }

            return true;
        });
    }

    private static bool Same(IReadOnlyList<Quantity> a, IReadOnlyList<Quantity> b) => a.SequenceEqual(b);

    private static decimal Total(IEnumerable<Quantity> quantities, UnitDimension dimension) =>
        quantities.Where(q => !q.IsToTaste && q.Unit.Dimension == dimension).Sum(q => q.BaseAmount!.Value);
}
