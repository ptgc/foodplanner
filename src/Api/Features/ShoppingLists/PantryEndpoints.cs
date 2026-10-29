using Foodplanner.Api.Authorization;
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

/// <summary>
/// Say "we have this" (T-037, T-093). <c>ExpiresAt</c> is optional and must be in the future; after it the pantry no longer
/// counts. <c>Quantity</c> and <c>UnitId</c> go together, or neither ("we have some", no amount). The request replaces the
/// whole entry: an amount and an expiry that are left out are cleared.
/// </summary>
public sealed record ConfirmPantryItemRequest(DateTimeOffset? ExpiresAt, decimal? Quantity = null, Guid? UnitId = null);

/// <summary>
/// <c>Quantity</c>, <c>UnitId</c>, <c>UnitCode</c> and <c>UnitName</c> are null for "we have some". <c>IsExpired</c>: the entry is
/// past its expiry and no longer counts (only listed with <c>includeExpired</c>).
/// </summary>
public sealed record PantryItemResponse(
    Guid IngredientId,
    string Name,
    DateTimeOffset ConfirmedAt,
    Guid ConfirmedByUserId,
    DateTimeOffset? ExpiresAt,
    decimal? Quantity,
    Guid? UnitId,
    string? UnitCode,
    string? UnitName,
    bool IsExpired);

/// <summary>
/// The household's pantry (T-037, T-093): any ingredient it has at home, optionally with an amount and an expiry. Every member,
/// Child included, may read, add, change and remove entries (family use, like check-off; the group default
/// <c>HouseholdMember</c>, no role is read). Clearing the whole pantry is Adult or Owner (policy <c>HouseholdAdult</c>). See docs/domain.md.
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

        pantry.MapDelete("", ClearPantry)
            .WithName("ClearPantry")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    /// <summary>
    /// The active entries (not expired), by name. Expired rows stay stored but are not listed unless <c>includeExpired</c> is
    /// true (the pantry screen, so they can be renewed or removed): they no longer count on the list.
    /// </summary>
    private static async Task<IResult> ListPantry(
        RequestLocale locale, TimeProvider time, AppDbContext db, CancellationToken cancellationToken, bool includeExpired = false)
    {
        var now = time.GetUtcNow();
        var items = await db.PantryItems.AsNoTracking().Where(p => includeExpired || p.ExpiresAt == null || p.ExpiresAt > now)
            .ToListAsync(cancellationToken);
        return Results.Ok(await ToResponsesAsync(db, items, now, locale.Code, cancellationToken));
    }

    private static async Task<IResult> ConfirmPantryItem(
        Guid ingredientId, ConfirmPantryItemRequest? request, RequestLocale locale, ICurrentUser user, TimeProvider time, AppDbContext db,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var errors = new Dictionary<string, string[]>();
        if (request?.ExpiresAt is { } expiry && expiry <= now)
        {
            errors["expiresAt"] = ["The expiry must be in the future."];
        }

        WeeklyGroceryEndpoints.ValidateQuantity(request?.Quantity, request?.UnitId, errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (user.UserId is not { } userId)
        {
            return Results.Unauthorized();
        }

        // Q-012 rule: ingredient and unit are resolved through the filtered sets (global or own) before anything is saved. Another
        // household's id is not found, the same answer as an unknown one.
        if (!await db.Ingredients.AsNoTracking().AnyAsync(i => i.Id == ingredientId, cancellationToken))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["ingredientId"] = ["The ingredient does not exist."] });
        }

        if (request?.UnitId is { } unitId && !await db.Units.AnyAsync(u => u.Id == unitId, cancellationToken))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["unitId"] = ["The unit does not exist."] });
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var item = await db.PantryItems.FirstOrDefaultAsync(p => p.IngredientId == ingredientId, cancellationToken);
            if (item is null)
            {
                item = new PantryItem(ingredientId, userId, now, request?.ExpiresAt, request?.Quantity, request?.UnitId);
                db.PantryItems.Add(item);
            }
            else
            {
                item.Confirm(userId, now, request?.ExpiresAt, request?.Quantity, request?.UnitId);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok((await ToResponsesAsync(db, [item], now, locale.Code, cancellationToken)).Single());
            }
            catch (DbUpdateException)
            {
                // Two members saved the same ingredient at once (unique index), or the row changed: the second pass
                // reads the winner's row and applies this request to it. A foreign key failure (the unit or the ingredient
                // was removed meanwhile) also lands here: the retry then ends in the 409 below.
                db.ChangeTracker.Clear();
            }
        }

        return AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.PantryConflict,
            "Pantry changed",
            "The pantry was changed by someone else at the same time. Try again.");
    }

    /// <summary>Idempotent: an ingredient that is not in the pantry is already removed.</summary>
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

    /// <summary>Removes every entry of the household (expired ones too). Idempotent.</summary>
    private static async Task<IResult> ClearPantry(AppDbContext db, CancellationToken cancellationToken)
    {
        db.PantryItems.RemoveRange(await db.PantryItems.ToListAsync(cancellationToken));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear(); // some rows were removed by someone else in between: gone either way
        }

        return Results.NoContent();
    }

    private static async Task<PantryItemResponse[]> ToResponsesAsync(
        AppDbContext db, IReadOnlyList<PantryItem> items, DateTimeOffset now, string locale, CancellationToken cancellationToken)
    {
        var ids = items.Select(i => i.IngredientId).ToList();
        var names = await db.Ingredients.AsNoTracking().Where(i => ids.Contains(i.Id))
            .NamesByIdAsync(db, CatalogTranslation.KindIngredient, locale, cancellationToken);
        var units = await db.Units.AsNoTracking().WithNames(db, CatalogTranslation.KindUnit, locale)
            .ToDictionaryAsync(u => u.Row.Id, u => (u.Row.Code, u.Name), cancellationToken);
        return [.. items
            .Select(i => new PantryItemResponse(
                i.IngredientId,
                names.GetValueOrDefault(i.IngredientId, string.Empty),
                i.ConfirmedAt,
                i.ConfirmedByUserId,
                i.ExpiresAt,
                i.Amount,
                i.UnitId,
                i.UnitId is { } u ? units.GetValueOrDefault(u).Code : null,
                i.UnitId is { } n ? units.GetValueOrDefault(n).Name : null,
                !i.IsActive(now)))
            .OrderBy(r => r.Name, StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, ignoreCase: true))
            .ThenBy(r => r.IngredientId)];
    }
}
