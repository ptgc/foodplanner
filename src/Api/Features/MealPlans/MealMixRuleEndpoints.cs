using Foodplanner.Api.Authorization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.MealPlans;
using Foodplanner.Domain.Recipes;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.MealPlans;

/// <summary>One rule: at least <c>MinPerWeek</c> dinners of <c>FoodType</c> each week.</summary>
public sealed record MealMixRuleResponse(FoodType FoodType, int MinPerWeek);

/// <summary>The household's rules, and how many dinner days a week has (the sum of the minimums cannot exceed it).</summary>
public sealed record MealMixRulesResponse(MealMixRuleResponse[] Rules, int DinnerDaysPerWeek);

/// <summary><c>FoodType</c> is a food type name other than Unknown (Fish, Seafood, Chicken, Beef, Pork, Lamb, MixedMeat, Vegetarian, Vegan).</summary>
public sealed record MealMixRuleRequest(string? FoodType, int? MinPerWeek);

/// <summary>The complete new set of rules; an empty list removes all rules.</summary>
public sealed record MealMixRulesRequest(MealMixRuleRequest[]? Rules);

/// <summary>
/// The household's weekly meal mix rules (T-090, Q-021): "at least N dinners of food type X each week". Any member reads them;
/// Adult and Owner replace them (policy <see cref="Policies.HouseholdAdult"/>, which kid mode never passes).
/// </summary>
public static class MealMixRuleEndpoints
{
    public static IEndpointRouteBuilder MapMealMixRuleEndpoints(this IEndpointRouteBuilder api)
    {
        var rules = api.MapHouseholdScopedGroup().MapGroup("/meal-mix-rules");

        rules.MapGet("", GetRules)
            .WithName("GetMealMixRules")
            .Produces<MealMixRulesResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        rules.MapPut("", PutRules)
            .WithName("PutMealMixRules")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces<MealMixRulesResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    internal static MealMixRulesResponse ToResponse(IEnumerable<MealMixRule> rows) =>
        new([.. rows.OrderBy(r => r.FoodType).Select(r => new MealMixRuleResponse(r.FoodType, r.MinPerWeek))], MealMix.DinnerDaysPerWeek);

    private static async Task<IResult> GetRules(AppDbContext db, CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await db.MealMixRules.AsNoTracking().ToListAsync(cancellationToken)));

    private static async Task<IResult> PutRules(MealMixRulesRequest? request, AppDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var wanted = new List<MealMixRequirement>();
        if (request?.Rules is not { } requested)
        {
            errors["rules"] = ["A list of rules is required (it can be empty)."];
        }
        else
        {
            for (var i = 0; i < requested.Length; i++)
            {
                var item = requested[i];
                if (item is null || !FoodTypes.TryParse(item.FoodType, out var type))
                {
                    errors[$"rules[{i}].foodType"] = ["Unknown food type."];
                }
                else if (item.MinPerWeek is not { } min)
                {
                    errors[$"rules[{i}].minPerWeek"] = ["A minimum is required."];
                }
                else
                {
                    wanted.Add(new MealMixRequirement(type, min));
                }
            }

            if (errors.Count == 0)
            {
                foreach (var (key, message) in MealMix.Validate(wanted))
                {
                    errors[key] = [message];
                }
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var existing = await db.MealMixRules.ToListAsync(cancellationToken);
        foreach (var row in existing.Where(r => wanted.All(w => w.FoodType != r.FoodType)))
        {
            db.MealMixRules.Remove(row);
        }

        foreach (var rule in wanted)
        {
            if (existing.FirstOrDefault(r => r.FoodType == rule.FoodType) is { } row)
            {
                row.ChangeMinimum(rule.MinPerWeek);
            }
            else
            {
                db.MealMixRules.Add(new MealMixRule(rule.FoodType, rule.MinPerWeek));
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException)
        {
            // Two adults saving at once: the unique (household, food type) index, or a row deleted under us.
            db.ChangeTracker.Clear();
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                AppProblems.MealMixRulesConflict,
                "The rules changed",
                "Someone else changed the meal mix rules at the same time. Reload and try again.");
        }

        return Results.Ok(ToResponse(await db.MealMixRules.AsNoTracking().ToListAsync(cancellationToken)));
    }
}
