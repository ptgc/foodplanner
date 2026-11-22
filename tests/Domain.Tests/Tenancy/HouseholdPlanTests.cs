using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Tests.Tenancy;

public class HouseholdPlanTests
{
    private static readonly HouseholdPlanLimits Defaults = new(AiLimit.Of(2), AiLimit.Of(20), AiLimit.Of(200));

    [Fact]
    public void HouseholdPlanLimits_Resolve_uses_defaults_without_a_plan()
    {
        Assert.Equal(Defaults, HouseholdPlanLimits.Resolve(null, Defaults));

        var plan = new HouseholdPlan("Family", new HouseholdPlanLimits(AiLimit.Unlimited, AiLimit.Of(40), AiLimit.Of(0)));
        var resolved = HouseholdPlanLimits.Resolve(plan, Defaults);

        Assert.True(resolved.PhotoImportsPerDay.IsUnlimited);
        Assert.Equal(40, resolved.LineHelpPerDay.Count);
        Assert.Equal(0, resolved.LineHelpPerMonth.Count);
        Assert.True(resolved.LineHelpPerMonth.IsNone);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    [InlineData(int.MinValue)]
    public void AiLimit_validation_rejects_numbers_outside_0_to_10000(int count)
    {
        Assert.False(AiLimit.IsValidCount(count));
        Assert.Throws<ArgumentOutOfRangeException>(() => AiLimit.Of(count));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10_000)]
    public void AiLimit_validation_allows_0_to_10000(int count)
    {
        Assert.Equal(count, AiLimit.Of(count).Count);
        Assert.False(AiLimit.Of(count).IsUnlimited);
    }

    [Fact]
    public void An_unlimited_limit_carries_no_number_and_never_fails_a_comparison()
    {
        Assert.Null(AiLimit.Unlimited.Count);
        Assert.True(AiLimit.Unlimited.IsUnlimited);
        Assert.Equal(int.MaxValue, AiLimit.Unlimited.ToCeiling());
        Assert.True(default(AiLimit).IsUnlimited);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(50_000, 50_000)]
    public void A_configured_default_is_not_range_checked_and_at_or_below_zero_means_none(int configured, int expected) =>
        Assert.Equal(expected, AiLimit.Configured(configured).Count);

    [Fact]
    public void Replace_rejects_a_name_over_40_characters_or_with_control_characters()
    {
        var plan = new HouseholdPlan("Family", Defaults);

        Assert.Throws<ArgumentException>(() => plan.Replace(new string('a', 41), Defaults));
        Assert.Throws<ArgumentException>(() => plan.Replace("bell\u0007", Defaults));
        Assert.Equal("Family", plan.Name);

        plan.Replace(new string('a', 40), Defaults);
        Assert.Equal(40, plan.Name!.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Replace_turns_an_empty_name_into_null(string? name)
    {
        var plan = new HouseholdPlan("Family", Defaults);

        plan.Replace(name, Defaults);

        Assert.Null(plan.Name);
    }

    [Fact]
    public void Replace_trims_the_name_and_replaces_every_limit()
    {
        var plan = new HouseholdPlan("  Unlimited ", new HouseholdPlanLimits(AiLimit.Unlimited, AiLimit.Unlimited, AiLimit.Unlimited));

        Assert.Equal("Unlimited", plan.Name);
        Assert.Null(plan.PhotoImportsPerDay);

        plan.Replace(null, Defaults);

        Assert.Equal(2, plan.PhotoImportsPerDay);
        Assert.Equal(20, plan.LineHelpPerDay);
        Assert.Equal(200, plan.LineHelpPerMonth);
        Assert.Equal(Defaults, plan.Limits);
    }
}
