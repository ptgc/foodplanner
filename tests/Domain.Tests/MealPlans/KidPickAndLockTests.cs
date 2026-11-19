using Foodplanner.Domain.MealPlans;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Tests.MealPlans;

public class KidPickAndLockTests
{
    private static readonly Guid Dinner = TagSeed.All.Single(t => t.Key == "middag").Id;
    private static readonly Guid Dessert = TagSeed.All.Single(t => t.Key == "dessert").Id;
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly KidPickPlan Empty = new(Monday, []);

    private static KidPickRecipe Recipe(params Guid[] categories) => new(Guid.NewGuid(), categories.ToHashSet());

    [Fact]
    public void The_default_rule_allows_dinners_only()
    {
        IKidPickRules rules = new DinnerOnlyKidPickRules(Dinner);

        Assert.True(rules.IsAllowed(Recipe(Dinner), Monday, Empty));
        Assert.True(rules.IsAllowed(Recipe(Dinner, Dessert), Monday, Empty)); // in Middag among other categories
        Assert.False(rules.IsAllowed(Recipe(Dessert), Monday, Empty));
        Assert.False(rules.IsAllowed(Recipe(), Monday, Empty));
    }

    [Fact]
    public void The_seam_can_be_replaced_by_a_rule_that_looks_at_the_plan()
    {
        // T-090 will do this: the plan is part of the question.
        var firstDinnerOnly = new FirstDinnerOnly();
        var dinner = Recipe(Dinner);

        Assert.True(firstDinnerOnly.IsAllowed(dinner, Monday, Empty));
        Assert.False(firstDinnerOnly.IsAllowed(dinner, Monday, new KidPickPlan(Monday, [new KidPickEntry(Monday, Guid.NewGuid())])));
    }

    private sealed class FirstDinnerOnly : IKidPickRules
    {
        public bool IsAllowed(KidPickRecipe recipe, DateOnly day, KidPickPlan plan) => plan.Entries.All(e => e.Date != day);
    }

    [Fact]
    public void Locking_records_who_and_when_and_keeps_the_first_lock()
    {
        var plan = new MealPlan(Monday);
        var first = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        Assert.False(plan.IsLocked);

        plan.Lock(at, first);
        plan.Lock(at.AddHours(1), Guid.NewGuid());

        Assert.True(plan.IsLocked);
        Assert.Equal(at, plan.LockedAt);
        Assert.Equal(first, plan.LockedByUserId);
        plan.Unlock();
        Assert.False(plan.IsLocked);
        Assert.Null(plan.LockedByUserId);
        plan.Unlock(); // idempotent
        Assert.Throws<ArgumentException>(() => plan.Lock(at, Guid.Empty));
    }

    [Fact]
    public void An_entry_may_be_picked_by_a_profile_and_the_profile_can_be_cleared()
    {
        var profile = Guid.NewGuid();
        var entry = new MealPlanEntry(Guid.NewGuid(), Monday, Monday, Guid.NewGuid(), 4, Guid.NewGuid(), profile);
        Assert.Equal(profile, entry.PickedByProfileId);

        entry.ClearPickedByProfile();

        Assert.Null(entry.PickedByProfileId);
        Assert.Null(new MealPlanEntry(Guid.NewGuid(), Monday, Monday, Guid.NewGuid(), 4, Guid.NewGuid()).PickedByProfileId);
        Assert.Throws<ArgumentException>(() => new MealPlanEntry(Guid.NewGuid(), Monday, Monday, Guid.NewGuid(), 4, Guid.NewGuid(), Guid.Empty));
    }

    [Theory]
    [InlineData(HouseholdRole.Owner, false, HouseholdRole.Owner)]
    [InlineData(HouseholdRole.Adult, false, HouseholdRole.Adult)]
    [InlineData(HouseholdRole.Child, false, HouseholdRole.Child)]
    [InlineData(HouseholdRole.Owner, true, HouseholdRole.Child)]
    [InlineData(HouseholdRole.Adult, true, HouseholdRole.Child)]
    [InlineData(HouseholdRole.Child, true, HouseholdRole.Child)]
    public void Kid_mode_narrows_every_role_to_child(HouseholdRole membership, bool kidMode, HouseholdRole expected) =>
        Assert.Equal(expected, membership.Effective(kidMode));
}
