using Foodplanner.Domain.MealPlans;

namespace Foodplanner.Domain.Tests.MealPlans;

public class MealPlanTests
{
    private static readonly DateOnly Monday = new(2026, 10, 26);

    [Fact]
    public void A_plan_starts_on_a_monday()
    {
        Assert.Equal(Monday, new MealPlan(Monday).WeekStart);
        Assert.Throws<ArgumentException>(() => new MealPlan(Monday.AddDays(1)));
        Assert.Throws<ArgumentException>(() => new MealPlan(Monday.AddDays(-1)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(-1, false)]
    public void The_week_is_monday_to_sunday(int offset, bool inWeek) =>
        Assert.Equal(inWeek, MealPlan.IsInWeek(Monday, Monday.AddDays(offset)));

    [Fact]
    public void An_entry_keeps_its_picker_recipe_date_and_servings()
    {
        var plan = Guid.NewGuid();
        var recipe = Guid.NewGuid();
        var user = Guid.NewGuid();

        var entry = new MealPlanEntry(plan, Monday, Monday.AddDays(2), recipe, 4, user);

        Assert.Equal((plan, recipe, user, 4, Monday.AddDays(2)), (entry.MealPlanId, entry.RecipeId, entry.PickedByUserId, entry.Servings, entry.Date));
    }

    [Fact]
    public void An_entry_outside_the_week_or_with_bad_servings_is_refused()
    {
        var entry = new MealPlanEntry(Guid.NewGuid(), Monday, Monday, Guid.NewGuid(), 1, Guid.NewGuid());

        Assert.Throws<ArgumentOutOfRangeException>(() => entry.Change(Monday, Monday.AddDays(7), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => entry.Change(Monday, Monday.AddDays(-1), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => entry.Change(Monday, Monday, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => entry.Change(Monday, Monday, MealPlanEntry.MaxServings + 1));
        entry.Change(Monday, Monday.AddDays(6), MealPlanEntry.MaxServings);
        Assert.Equal((Monday.AddDays(6), MealPlanEntry.MaxServings), (entry.Date, entry.Servings));
    }

    [Fact]
    public void An_entry_needs_plan_recipe_and_picker()
    {
        Assert.Throws<ArgumentException>(() => new MealPlanEntry(Guid.Empty, Monday, Monday, Guid.NewGuid(), 1, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new MealPlanEntry(Guid.NewGuid(), Monday, Monday, Guid.Empty, 1, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new MealPlanEntry(Guid.NewGuid(), Monday, Monday, Guid.NewGuid(), 1, Guid.Empty));
    }
}
