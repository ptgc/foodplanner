using Foodplanner.Domain.MealPlans;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Tests.MealPlans;

public class MealMixTests
{
    private static readonly MealMixRequirement[] FishAndVegetarian =
        [new(FoodType.Fish, 1), new(FoodType.Vegetarian, 1)];

    private static FoodType[] Meat(int n) => [.. Enumerable.Repeat(FoodType.Beef, n)];

    [Fact]
    public void The_T090_example_five_meat_dinners_leave_only_fish_and_vegetarian_for_the_last_two_days()
    {
        var allowed = MealMix.AllowedTypes(FishAndVegetarian, openDays: 2, Meat(5));

        Assert.Equal([FoodType.Fish, FoodType.Vegetarian], allowed);
        Assert.False(MealMix.CanAdd(FishAndVegetarian, 2, Meat(5), FoodType.Chicken));
        Assert.False(MealMix.CanAdd(FishAndVegetarian, 2, Meat(5), FoodType.Unknown));
        Assert.False(MealMix.CanAdd(FishAndVegetarian, 2, Meat(5), FoodType.Vegan));
    }

    [Fact]
    public void After_the_fish_the_last_day_must_be_vegetarian()
    {
        var planned = Meat(5).Append(FoodType.Fish).ToArray();

        Assert.Equal([FoodType.Vegetarian], MealMix.AllowedTypes(FishAndVegetarian, openDays: 1, planned));
    }

    [Fact]
    public void With_room_to_spare_anything_is_allowed()
    {
        Assert.Equal(Enum.GetValues<FoodType>(), MealMix.AllowedTypes(FishAndVegetarian, openDays: 7, []));
        Assert.Equal(Enum.GetValues<FoodType>(), MealMix.AllowedTypes(FishAndVegetarian, openDays: 3, Meat(4)));
    }

    [Fact]
    public void With_exactly_as_many_open_days_as_missing_dinners_only_the_missing_types_fit()
    {
        var rules = new MealMixRequirement[] { new(FoodType.Fish, 2), new(FoodType.Chicken, 1) };

        Assert.Equal([FoodType.Fish, FoodType.Chicken], MealMix.AllowedTypes(rules, openDays: 3, []));
        Assert.Equal(Enum.GetValues<FoodType>(), MealMix.AllowedTypes(rules, openDays: 3, [FoodType.Fish, FoodType.Chicken, FoodType.Fish]));
        Assert.Equal([FoodType.Fish], MealMix.AllowedTypes(rules, openDays: 2, [FoodType.Chicken]));
    }

    [Fact]
    public void Fewer_open_days_than_the_rules_need_is_infeasible_and_nothing_makes_it_worse()
    {
        // 1 day left, 2 fish still missing: already unmeetable (an adult overrode a rule).
        var rules = new MealMixRequirement[] { new(FoodType.Fish, 2) };

        Assert.False(MealMix.IsFeasible(rules, openDays: 1, []));
        Assert.Equal([FoodType.Fish], MealMix.AllowedTypes(rules, openDays: 1, [])); // does not worsen the slack
        Assert.False(MealMix.CanAdd(rules, 1, [], FoodType.Beef));
    }

    [Fact]
    public void Zero_rules_allow_everything_even_a_full_week()
    {
        Assert.Equal(Enum.GetValues<FoodType>(), MealMix.AllowedTypes([], openDays: 1, []));
        Assert.Equal(Enum.GetValues<FoodType>(), MealMix.AllowedTypes([], openDays: 0, Meat(7), fillsOpenDay: false));
        Assert.True(MealMix.IsFeasible([], 0, []));
    }

    [Fact]
    public void Rules_already_met_allow_everything()
    {
        var planned = new[] { FoodType.Fish, FoodType.Vegetarian, FoodType.Beef };

        Assert.Equal(Enum.GetValues<FoodType>(), MealMix.AllowedTypes(FishAndVegetarian, openDays: 1, planned));
        Assert.True(MealMix.IsFeasible(FishAndVegetarian, 0, planned));
    }

    [Fact]
    public void Unknown_counts_toward_no_rule_but_uses_a_day()
    {
        Assert.Equal(1, MealMix.Slack(FishAndVegetarian, openDays: 3, [FoodType.Unknown]));
        Assert.False(MealMix.CanAdd(FishAndVegetarian, openDays: 2, [], FoodType.Unknown));
        Assert.True(MealMix.CanAdd(FishAndVegetarian, openDays: 3, [], FoodType.Unknown));
        Assert.Equal(0, MealMix.Slack([], 0, [FoodType.Unknown]));
    }

    [Fact]
    public void A_type_counts_only_for_itself_vegan_is_not_vegetarian()
    {
        Assert.False(MealMix.IsFeasible([new(FoodType.Vegetarian, 1)], openDays: 0, [FoodType.Vegan]));
        Assert.True(MealMix.IsFeasible([new(FoodType.Vegetarian, 1)], openDays: 0, [FoodType.Vegetarian]));
    }

    [Fact]
    public void Removing_a_dinner_frees_capacity()
    {
        var withFish = Meat(5).Append(FoodType.Fish).ToArray();
        Assert.Equal([FoodType.Vegetarian], MealMix.AllowedTypes(FishAndVegetarian, openDays: 1, withFish));

        // The fish dinner is removed: its day is open again and fish is required again.
        Assert.Equal([FoodType.Fish, FoodType.Vegetarian], MealMix.AllowedTypes(FishAndVegetarian, openDays: 2, Meat(5)));
        // A meat dinner is removed: one more day for anything.
        Assert.True(MealMix.CanAdd(FishAndVegetarian, 3, Meat(4), FoodType.Beef));
    }

    [Fact]
    public void A_second_dinner_on_a_day_that_has_one_fills_no_open_day()
    {
        // 2 open days, fish and vegetarian missing: a meat dinner on an already filled day does not need a free day.
        Assert.True(MealMix.CanAdd(FishAndVegetarian, openDays: 2, Meat(5), FoodType.Beef, fillsOpenDay: false));
        Assert.False(MealMix.CanAdd(FishAndVegetarian, openDays: 2, Meat(5), FoodType.Beef, fillsOpenDay: true));
    }

    [Fact]
    public void Validation_accepts_a_valid_set_and_an_empty_one()
    {
        Assert.Empty(MealMix.Validate([]));
        Assert.Empty(MealMix.Validate(FishAndVegetarian));
        Assert.Empty(MealMix.Validate([new(FoodType.Fish, 7)]));
    }

    [Fact]
    public void Validation_rejects_a_sum_above_the_dinner_days()
    {
        var errors = MealMix.Validate([new(FoodType.Fish, 4), new(FoodType.Chicken, 4)]);

        Assert.Contains("rules", errors.Keys);
    }

    [Fact]
    public void Validation_rejects_duplicates_bad_minimums_and_unknown()
    {
        Assert.Contains("rules[1].foodType", MealMix.Validate([new(FoodType.Fish, 1), new(FoodType.Fish, 2)]).Keys);
        Assert.Contains("rules[0].minPerWeek", MealMix.Validate([new(FoodType.Fish, 0)]).Keys);
        Assert.Contains("rules[0].minPerWeek", MealMix.Validate([new(FoodType.Fish, 8)]).Keys);
        Assert.Contains("rules[0].foodType", MealMix.Validate([new(FoodType.Unknown, 1)]).Keys);
        Assert.Contains("rules[0].foodType", MealMix.Validate([new((FoodType)99, 1)]).Keys);
    }

    [Fact]
    public void The_rule_entity_guards_its_minimum_and_type()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MealMixRule(FoodType.Fish, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MealMixRule(FoodType.Fish, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MealMixRule((FoodType)99, 1));
        var rule = new MealMixRule(FoodType.Fish, 1);
        rule.ChangeMinimum(3);
        Assert.Equal(3, rule.MinPerWeek);
        Assert.Throws<ArgumentOutOfRangeException>(() => rule.ChangeMinimum(9));
        Assert.IsAssignableFrom<ITenantEntity>(rule);
    }
}

public class MealMixKidPickRulesTests
{
    private static readonly Guid Dinner = TagSeed.All.Single(t => t.Key == "middag").Id;
    private static readonly Guid Dessert = TagSeed.All.Single(t => t.Key == "dessert").Id;
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly MealMixRequirement[] Rules = [new(FoodType.Fish, 1), new(FoodType.Vegetarian, 1)];
    private readonly MealMixKidPickRules _rules = new(Dinner);

    private static KidPickRecipe Recipe(FoodType type, params Guid[] categories) => new(Guid.NewGuid(), categories.ToHashSet(), type);

    private static KidPickPlan Plan(int meatDinners, params FoodType[] others)
    {
        var entries = Enumerable.Range(0, meatDinners).Select(i => new KidPickEntry(Monday.AddDays(i), Guid.NewGuid(), true, FoodType.Beef))
            .Concat(others.Select((t, i) => new KidPickEntry(Monday.AddDays(meatDinners + i), Guid.NewGuid(), true, t)));
        return new KidPickPlan(Monday, [.. entries], Rules);
    }

    [Fact]
    public void Without_rules_it_is_dinner_only()
    {
        var plan = new KidPickPlan(Monday, []);

        Assert.True(_rules.IsAllowed(Recipe(FoodType.Beef, Dinner), Monday, plan));
        Assert.False(_rules.IsAllowed(Recipe(FoodType.Beef, Dessert), Monday, plan));
        Assert.False(_rules.IsAllowed(Recipe(FoodType.Fish), Monday, plan));
    }

    [Fact]
    public void A_kid_cannot_pick_meat_for_the_last_two_days_but_can_pick_fish_or_vegetarian()
    {
        var plan = Plan(5);
        var day = Monday.AddDays(5);

        Assert.False(_rules.IsAllowed(Recipe(FoodType.Chicken, Dinner), day, plan));
        Assert.True(_rules.IsAllowed(Recipe(FoodType.Fish, Dinner), day, plan));
        Assert.True(_rules.IsAllowed(Recipe(FoodType.Vegetarian, Dinner), day, plan));
    }

    [Fact]
    public void Non_dinners_are_not_part_of_the_mix_for_the_fit_check_but_kids_cannot_pick_them()
    {
        var plan = Plan(5);
        var dessert = Recipe(FoodType.Beef, Dessert);

        Assert.True(_rules.FitsMealMix(dessert, Monday.AddDays(5), plan));
        Assert.False(_rules.IsAllowed(dessert, Monday.AddDays(5), plan));
    }

    [Fact]
    public void Planned_non_dinners_do_not_use_up_a_dinner_day()
    {
        var entries = Enumerable.Range(0, 5).Select(i => new KidPickEntry(Monday.AddDays(i), Guid.NewGuid(), true, FoodType.Beef))
            .Append(new KidPickEntry(Monday.AddDays(5), Guid.NewGuid(), false, FoodType.Beef)); // a dessert on Saturday
        var plan = new KidPickPlan(Monday, [.. entries], Rules);

        Assert.True(_rules.IsAllowed(Recipe(FoodType.Fish, Dinner), Monday.AddDays(5), plan));
        Assert.False(_rules.IsAllowed(Recipe(FoodType.Beef, Dinner), Monday.AddDays(5), plan));
    }

    [Fact]
    public void A_second_dinner_on_a_filled_day_is_judged_without_using_an_open_day()
    {
        var plan = Plan(5);

        // Monday already has a dinner: another meat dinner there takes no open day.
        Assert.True(_rules.IsAllowed(Recipe(FoodType.Beef, Dinner), Monday, plan));
    }

    [Fact]
    public void The_interface_is_the_one_the_endpoints_use()
    {
        IKidPickRules rules = _rules;

        Assert.False(rules.IsAllowed(Recipe(FoodType.Unknown, Dinner), Monday.AddDays(5), Plan(5)));
    }
}
