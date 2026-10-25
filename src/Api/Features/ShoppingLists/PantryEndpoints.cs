using Foodplanner.Api.Localization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.ShoppingLists;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Localization;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.ShoppingLists;

/// <summary>Confirm "I have it". <c>ExpiresAt</c> is optional and must be in the future; after it the staple is prompted again.</summary>
public sealed record ConfirmPantryItemRequest(DateTimeOffset? ExpiresAt);

public sealed record PantryItemResponse(
    Guid IngredientId, string Name, DateTimeOffset ConfirmedAt, Guid ConfirmedByUserId, DateTimeOffset? ExpiresAt);

/// <summary>
/// Pantry memory (T-037): the staples a household has confirmed. Every member, Child included, may confirm and
/// un-confirm (family use, like check-off; the group default <c>HouseholdMember</c>, no role is read). See docs/domain.md.
/// </summary>
public static class PantryEndpoints
{
    public static IEndpointRouteBuilder MapPantryEndpoints(this IEndpointRouteBuilder api)
    {
        var pantry = api.MapHouseholdScopedGroup().MapGroup("/pantry");

        pantry.MapGet("", ListPantry)
            .WithName("ListPantry")
            .Produces<PantryItemResponse[]>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        pantry.MapPut("/{ingredientId:guid}", ConfirmPantryItem)
            .WithName("ConfirmPantryItem")
            .Produces<PantryItemResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        pantry.MapDelete("/{ingredientId:guid}", RemovePantryItem)
            .WithName("RemovePantryItem")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    /// <summary>The active entries (not expired), by name. Expired rows stay stored but are not listed: the staple is prompted again.</summary>
    private static async Task<IResult> ListPantry(RequestLocale locale, TimeProvider time, AppDbContext db, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var items = await db.PantryItems.AsNoTracking().Where(p => p.ExpiresAt == null || p.ExpiresAt > now).ToListAsync(cancellationToken);
        return Results.Ok(await ToResponsesAsync(db, items, locale.Code, cancellationToken));
    }

    private static async Task<IResult> ConfirmPantryItem(
        Guid ingredientId, ConfirmPantryItemRequest? request, RequestLocale locale, ICurrentUser user, TimeProvider time, AppDbContext db,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (request?.ExpiresAt is { } expiry && expiry <= now)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["expiresAt"] = ["The expiry must be in the future."] });
        }

        if (user.UserId is not { } userId)
        {
            return Results.Unauthorized();
        }

        // Q-012 rule: the ingredient is resolved through the filtered set (global or own) before anything is saved. Another
        // household's id is not found, the same answer as an unknown one.
        var ingredient = await db.Ingredients.AsNoTracking().FirstOrDefaultAsync(i => i.Id == ingredientId, cancellationToken);
        if (ingredient is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["ingredientId"] = ["The ingredient does not exist."] });
        }

        if (!ingredient.IsStaple)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["ingredientId"] = ["Only staples can be confirmed."] });
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var item = await db.PantryItems.FirstOrDefaultAsync(p => p.IngredientId == ingredientId, cancellationToken);
            if (item is null)
            {
                item = new PantryItem(ingredientId, userId, now, request?.ExpiresAt);
                db.PantryItems.Add(item);
            }
            else
            {
                item.Confirm(userId, now, request?.ExpiresAt);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok((await ToResponsesAsync(db, [item], locale.Code, cancellationToken)).Single());
            }
            catch (DbUpdateException)
            {
                // Two members confirmed the same staple at once (unique index), or the row changed: the second pass
                // reads the winner's row and applies this request to it.
                db.ChangeTracker.Clear();
            }
        }

        return AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.PantryConflict,
            "Pantry changed",
            "The pantry was changed by someone else at the same time. Try again.");
    }

    /// <summary>Idempotent: a staple that is not confirmed is already un-confirmed.</summary>
    private static async Task<IResult> RemovePantryItem(Guid ingredientId, AppDbContext db, CancellationToken cancellationToken)
    {
        var item = await db.PantryItems.FirstOrDefaultAsync(p => p.IngredientId == ingredientId, cancellationToken);
        if (item is not null)
        {
            db.PantryItems.Remove(item);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Removed by someone else in between: gone either way.
                db.ChangeTracker.Clear();
            }
        }

        return Results.NoContent();
    }

    private static async Task<PantryItemResponse[]> ToResponsesAsync(
        AppDbContext db, IReadOnlyList<PantryItem> items, string locale, CancellationToken cancellationToken)
    {
        var ids = items.Select(i => i.IngredientId).ToList();
        var names = await db.Ingredients.AsNoTracking().Where(i => ids.Contains(i.Id))
            .NamesByIdAsync(db, CatalogTranslation.KindIngredient, locale, cancellationToken);
        return [.. items
            .Select(i => new PantryItemResponse(i.IngredientId, names.GetValueOrDefault(i.IngredientId, string.Empty), i.ConfirmedAt, i.ConfirmedByUserId, i.ExpiresAt))
            .OrderBy(r => r.Name, StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, ignoreCase: true))
            .ThenBy(r => r.IngredientId)];
    }
}
