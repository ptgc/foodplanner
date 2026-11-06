using Foodplanner.Api.Authorization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.ShoppingLists;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.ShoppingLists;

/// <summary>
/// A label the household adds by hand again and again (T-110). <c>Label</c> is its most recent spelling, <c>Frequency</c> the
/// rhythm it follows, seen in <c>WeeksSeen</c> of the <c>WeeksLooked</c> weeks looked at.
/// </summary>
public sealed record GrocerySuggestionResponse(string Label, GroceryFrequency Frequency, int WeeksSeen, int WeeksLooked);

/// <summary>Dismiss the suggestion for this label (as typed; the server normalises it, 1-100 characters like a manual item).</summary>
public sealed record DismissGrocerySuggestionRequest(string? Label);

/// <summary>
/// Suggestions of regular groceries learnt from the items a household adds by hand (T-110, Q-030). Nothing is added unasked:
/// accepting is the client calling <c>POST /weekly-groceries</c> with the chosen ingredient and frequency. Owner and Adult only
/// (policy <see cref="Policies.HouseholdAdult"/>: a Child and kid mode are refused, as for the other writes of the set). Labels are
/// free text from people and are never logged.
/// </summary>
public static class GrocerySuggestionEndpoints
{
    public static IEndpointRouteBuilder MapGrocerySuggestionEndpoints(this IEndpointRouteBuilder api)
    {
        // A literal segment next to /weekly-groceries/{id:guid}: "suggestions" is no guid, so the routes never clash.
        var suggestions = api.MapHouseholdScopedGroup().MapGroup("/weekly-groceries/suggestions");

        suggestions.MapGet("", ListSuggestions)
            .WithName("ListGrocerySuggestions")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces<GrocerySuggestionResponse[]>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        suggestions.MapPost("/dismiss", Dismiss)
            .WithName("DismissGrocerySuggestion")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    /// <summary>
    /// A fixed number of queries whatever the history: the manual items of the window (one join), the regular groceries'
    /// ingredient ids, their names (own name plus every locale) and the active dismissals.
    /// </summary>
    private static async Task<IResult> ListSuggestions(TimeProvider time, AppDbContext db, CancellationToken cancellationToken)
    {
        var rules = SuggestionRules.Default;
        var currentWeek = GroceryCalendar.CurrentWeek(time);
        var first = RegularGrocerySuggester.WindowStart(currentWeek, rules);

        // Only items added by hand: no ingredient. Generated items (recipes) and weekly parts always have one.
        var purchases = await (
            from item in db.ShoppingListItems.AsNoTracking()
            join list in db.ShoppingLists.AsNoTracking() on item.ShoppingListId equals list.Id
            where item.IngredientId == null && item.Label != null && list.WeekStart >= first && list.WeekStart <= currentWeek
            select new { list.WeekStart, item.Label })
            .ToListAsync(cancellationToken);

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var groceryIngredients = await db.WeeklyGroceries.AsNoTracking().Select(w => w.IngredientId).ToListAsync(cancellationToken);
        if (groceryIngredients.Count > 0)
        {
            foreach (var name in await db.Ingredients.AsNoTracking().Where(i => groceryIngredients.Contains(i.Id)).Select(i => i.Name)
                         .ToListAsync(cancellationToken))
            {
                excluded.Add(GroceryLabel.Normalize(name));
            }

            foreach (var name in await db.CatalogTranslations.AsNoTracking()
                         .Where(t => t.Kind == CatalogTranslation.KindIngredient && groceryIngredients.Contains(t.EntityId))
                         .Select(t => t.Name).ToListAsync(cancellationToken))
            {
                excluded.Add(GroceryLabel.Normalize(name));
            }
        }

        var cutoff = time.GetUtcNow().AddDays(-7 * rules.HiddenWeeks);
        foreach (var key in await db.DismissedGrocerySuggestions.AsNoTracking().Where(d => d.DismissedAt > cutoff).Select(d => d.Label)
                     .ToListAsync(cancellationToken))
        {
            excluded.Add(key);
        }

        excluded.Remove(string.Empty);
        var suggestions = RegularGrocerySuggester.Suggest(
            purchases.Select(p => new ManualPurchase(p.WeekStart, p.Label!)), currentWeek, excluded, rules);
        return Results.Ok(suggestions.Select(s => new GrocerySuggestionResponse(s.Label, s.Frequency, s.WeeksSeen, s.WeeksLooked)).ToArray());
    }

    /// <summary>Idempotent: dismissing again renews the 12 weeks. A lost race on the unique index is the same outcome, not an error.</summary>
    private static async Task<IResult> Dismiss(
        DismissGrocerySuggestionRequest? request, TimeProvider time, AppDbContext db, CancellationToken cancellationToken)
    {
        var typed = ShoppingListItem.NormalizeLabel(request?.Label);
        var key = GroceryLabel.Normalize(typed);
        if (typed is null || key.Length == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["label"] = [$"The label is required, at most {ShoppingListItem.MaxLabelLength} characters, with a letter or digit and no control characters."],
            });
        }

        var now = time.GetUtcNow();
        var cutoff = now.AddDays(-7 * SuggestionRules.Default.HiddenWeeks);

        // Expired dismissals are of no use any more; removing them here keeps the table small without a background job.
        var expired = await db.DismissedGrocerySuggestions.Where(d => d.DismissedAt <= cutoff && d.Label != key).ToListAsync(cancellationToken);
        db.DismissedGrocerySuggestions.RemoveRange(expired);

        var existing = await db.DismissedGrocerySuggestions.FirstOrDefaultAsync(d => d.Label == key, cancellationToken);
        if (existing is not null)
        {
            existing.Renew(now);
        }
        else
        {
            if (await db.DismissedGrocerySuggestions.CountAsync(cancellationToken) - expired.Count >= DismissedGrocerySuggestion.MaxPerHousehold)
            {
                return AppProblems.Problem(
                    StatusCodes.Status409Conflict,
                    AppProblems.DismissedSuggestionLimit,
                    "Too many dismissed suggestions",
                    $"A household can keep at most {DismissedGrocerySuggestion.MaxPerHousehold} dismissed suggestions.");
            }

            db.DismissedGrocerySuggestions.Add(new DismissedGrocerySuggestion(key, now));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two members dismissed the same label at once (unique index), or an expired row was removed by the other request:
            // the label is hidden either way. Renew the winner's row when it is there; otherwise leave it.
            db.ChangeTracker.Clear();
            var winner = await db.DismissedGrocerySuggestions.FirstOrDefaultAsync(d => d.Label == key, cancellationToken);
            if (winner is not null)
            {
                winner.Renew(now);
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    db.ChangeTracker.Clear();
                }
            }
        }

        return Results.NoContent();
    }
}
