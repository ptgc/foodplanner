using Foodplanner.Domain.Quantities;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Tests.Recipes;

public class RecipeScalingTests
{
    private static readonly UnitCatalog Catalog = UnitCatalog.Seed;

    private static Quantity Q(decimal? amount, string unit) => new(amount, Catalog.Get(unit));

    [Fact]
    public void Four_to_six_servings_scales_by_one_and_a_half()
    {
        Assert.Equal(Q(300m, "g"), RecipeScaling.Scale(Q(200m, "g"), 4, 6));
        Assert.Equal(Q(3.75m, "dl"), RecipeScaling.Scale(Q(2.5m, "dl"), 4, 6));
    }

    [Fact]
    public void Same_servings_is_unchanged_and_fewer_servings_scales_down()
    {
        Assert.Equal(Q(200m, "g"), RecipeScaling.Scale(Q(200m, "g"), 4, 4));
        Assert.Equal(Q(50m, "g"), RecipeScaling.Scale(Q(200m, "g"), 4, 1));
    }

    [Fact]
    public void To_taste_is_unchanged()
    {
        var salt = Q(null, "stk");
        Assert.Equal(salt, RecipeScaling.Scale(salt, 4, 10));
        Assert.Equal(salt, RecipeScaling.ScaleForDisplay(salt, 4, 10, Catalog));
    }

    [Fact]
    public void Scaling_is_exact_and_display_rounds_afterwards()
    {
        // 100 g * 1/3 stays exact; only ForDisplay rounds (to 1 g).
        Assert.NotEqual(Q(33m, "g"), RecipeScaling.Scale(Q(100m, "g"), 3, 1));
        Assert.Equal(Q(33m, "g"), RecipeScaling.ScaleForDisplay(Q(100m, "g"), 3, 1, Catalog));
    }

    [Fact]
    public void Display_picks_kilograms_and_liters_from_a_thousand()
    {
        Assert.Equal(Q(1.5m, "kg"), RecipeScaling.ScaleForDisplay(Q(500m, "g"), 2, 6, Catalog));
        Assert.Equal(Q(1.2m, "l"), RecipeScaling.ScaleForDisplay(Q(4m, "dl"), 1, 3, Catalog));
        Assert.Equal(Q(750m, "g"), RecipeScaling.ScaleForDisplay(Q(1.5m, "kg"), 4, 2, Catalog));
    }

    [Fact]
    public void Count_units_round_to_their_step_and_never_to_zero()
    {
        Assert.Equal(Q(3m, "stk"), RecipeScaling.ScaleForDisplay(Q(2m, "stk"), 4, 6, Catalog));
        Assert.Equal(Q(0.5m, "stk"), RecipeScaling.ScaleForDisplay(Q(1m, "stk"), 100, 1, Catalog));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(-1, 4)]
    public void Non_positive_servings_throw(int baseServings, int servings) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipeScaling.Scale(Q(1m, "g"), baseServings, servings));
}
