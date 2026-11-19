using Foodplanner.Domain.Common;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.MealPlans;

/// <summary>
/// One weekly meal mix rule of a household (T-090, Q-021): "at least <see cref="MinPerWeek"/> dinners of
/// <see cref="FoodType"/> each week". Only minimums exist. Unique per (household, food type); HouseholdId is assigned by
/// Infrastructure.
/// </summary>
public sealed class MealMixRule : Entity, ITenantEntity
{
    private MealMixRule()
    {
    }

    public MealMixRule(FoodType foodType, int minPerWeek)
    {
        if (!Enum.IsDefined(foodType))
        {
            throw new ArgumentOutOfRangeException(nameof(foodType));
        }

        CheckMinimum(minPerWeek);
        FoodType = foodType;
        MinPerWeek = minPerWeek;
    }

    public Guid HouseholdId { get; private set; }

    public FoodType FoodType { get; private set; }

    public int MinPerWeek { get; private set; }

    public void ChangeMinimum(int minPerWeek)
    {
        CheckMinimum(minPerWeek);
        MinPerWeek = minPerWeek;
    }

    private static void CheckMinimum(int minPerWeek)
    {
        if (minPerWeek is < MealMix.MinRuleCount or > MealMix.DinnerDaysPerWeek)
        {
            throw new ArgumentOutOfRangeException(nameof(minPerWeek));
        }
    }
}

/// <summary>A rule as the pure feasibility logic sees it.</summary>
public readonly record struct MealMixRequirement(FoodType FoodType, int MinPerWeek);

/// <summary>
/// The pure feasibility logic of the weekly meal mix (T-090). The week has <see cref="DinnerDaysPerWeek"/> dinner days. A day
/// is open when it has no dinner yet; a dinner picked on a day that already has one fills no further day. Each recipe has one
/// food type, so the shortfalls of the rules add up: the rules can still be met when the sum of the shortfalls (per rule, the
/// minimum minus what is planned of that type, never below 0) is at most the number of open days. Unknown counts toward no rule
/// (it uses a day without helping), and a type counts only for itself (Vegan does not count as Vegetarian).
/// </summary>
public static class MealMix
{
    public const int DinnerDaysPerWeek = 7;
    public const int MinRuleCount = 1;

    /// <summary>The problems with a set of rules (empty when valid): duplicates, bad minimums, a sum above the dinner days. Keyed by a field path.</summary>
    public static IReadOnlyDictionary<string, string> Validate(IReadOnlyList<MealMixRequirement> rules)
    {
        var errors = new Dictionary<string, string>();
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (!Enum.IsDefined(rule.FoodType) || rule.FoodType == FoodType.Unknown)
            {
                errors[$"rules[{i}].foodType"] = "Choose a food type other than Unknown.";
            }
            else if (rules.Take(i).Any(r => r.FoodType == rule.FoodType))
            {
                errors[$"rules[{i}].foodType"] = "Each food type can have only one rule.";
            }

            if (rule.MinPerWeek is < MinRuleCount or > DinnerDaysPerWeek)
            {
                errors[$"rules[{i}].minPerWeek"] = $"The minimum must be {MinRuleCount} to {DinnerDaysPerWeek}.";
            }
        }

        if (errors.Count == 0 && rules.Sum(r => r.MinPerWeek) > DinnerDaysPerWeek)
        {
            errors["rules"] = $"The minimums add up to more than the {DinnerDaysPerWeek} dinners of a week.";
        }

        return errors;
    }

    /// <summary>Open dinner days minus the sum of the shortfalls: 0 or more means the rules can still be met.</summary>
    public static int Slack(IReadOnlyList<MealMixRequirement> rules, int openDays, IEnumerable<FoodType> planned)
    {
        var counts = planned.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        var shortfall = rules.Sum(r => Math.Max(0, r.MinPerWeek - counts.GetValueOrDefault(r.FoodType)));
        return openDays - shortfall;
    }

    /// <summary>True when the rules can still be met with <paramref name="openDays"/> days left and <paramref name="planned"/> dinners made.</summary>
    public static bool IsFeasible(IReadOnlyList<MealMixRequirement> rules, int openDays, IEnumerable<FoodType> planned) =>
        Slack(rules, openDays, planned) >= 0;

    /// <summary>
    /// May a dinner of <paramref name="candidate"/> be added without making the rules harder to meet? <paramref name="fillsOpenDay"/>
    /// says whether it goes on a day with no dinner yet. Allowed when the week stays feasible afterwards, or, when it already was
    /// not (an adult overrode a rule), when the slack does not get worse.
    /// </summary>
    public static bool CanAdd(
        IReadOnlyList<MealMixRequirement> rules, int openDays, IReadOnlyCollection<FoodType> planned, FoodType candidate, bool fillsOpenDay = true)
    {
        var before = Slack(rules, openDays, planned);
        var after = Slack(rules, fillsOpenDay ? Math.Max(0, openDays - 1) : openDays, planned.Append(candidate));
        return after >= Math.Min(0, before);
    }

    /// <summary>The food types that can still be added to a day (with <paramref name="fillsOpenDay"/> as in <see cref="CanAdd"/>), in enum order.</summary>
    public static IReadOnlyList<FoodType> AllowedTypes(
        IReadOnlyList<MealMixRequirement> rules, int openDays, IReadOnlyCollection<FoodType> planned, bool fillsOpenDay = true) =>
        [.. Enum.GetValues<FoodType>().Where(t => CanAdd(rules, openDays, planned, t, fillsOpenDay))];
}
