using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Households;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Profiles;

/// <summary>
/// Likes (T-026). A kid-mode session likes and unlikes recipes as its ACTIVE profile (taken from the cookie claim, never from
/// the request). PUT and DELETE are idempotent, so a second tap on the heart is just "unlike".
/// </summary>
public static class RecipeLikeEndpoints
{
    public static IEndpointRouteBuilder MapRecipeLikeEndpoints(this IEndpointRouteBuilder api)
    {
        var like = api.MapHouseholdScopedGroup().MapGroup("/recipes/{recipeId:guid}/like");

        like.MapPut("", Like)
            .WithName("LikeRecipe")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        like.MapDelete("", Unlike)
            .WithName("UnlikeRecipe")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static async Task<IResult> Like(Guid householdId, Guid recipeId, ICurrentUser user, AppDbContext db, CancellationToken cancellationToken)
    {
        if (await ActiveProfileAsync(householdId, user, db, cancellationToken) is not { } profileId)
        {
            return NoActiveProfile();
        }

        if (!await db.Recipes.AsNoTracking().AnyAsync(r => r.Id == recipeId, cancellationToken))
        {
            return RecipeNotFound();
        }

        if (await db.RecipeLikes.AnyAsync(l => l.MemberProfileId == profileId && l.RecipeId == recipeId, cancellationToken))
        {
            return Results.NoContent();
        }

        db.RecipeLikes.Add(new RecipeLike(profileId, recipeId));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear(); // a parallel tap won: the like exists, which is what was asked for
        }
        catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 547 })
        {
            db.ChangeTracker.Clear(); // the recipe or the profile was deleted in between
            return RecipeNotFound();
        }

        return Results.NoContent();
    }

    private static async Task<IResult> Unlike(Guid householdId, Guid recipeId, ICurrentUser user, AppDbContext db, CancellationToken cancellationToken)
    {
        if (await ActiveProfileAsync(householdId, user, db, cancellationToken) is not { } profileId)
        {
            return NoActiveProfile();
        }

        if (!await db.Recipes.AsNoTracking().AnyAsync(r => r.Id == recipeId, cancellationToken))
        {
            return RecipeNotFound();
        }

        var like = await db.RecipeLikes.FirstOrDefaultAsync(l => l.MemberProfileId == profileId && l.RecipeId == recipeId, cancellationToken);
        if (like is null)
        {
            return Results.NoContent();
        }

        db.RecipeLikes.Remove(like);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear(); // already gone
        }

        return Results.NoContent();
    }

    /// <summary>
    /// The active profile when the session is in kid mode for THIS household and the profile still exists in it; otherwise
    /// null (an adult, a real Child member outside kid mode, another household's profile, or a profile deleted elsewhere).
    /// </summary>
    private static async Task<Guid?> ActiveProfileAsync(Guid householdId, ICurrentUser user, AppDbContext db, CancellationToken cancellationToken)
    {
        if (user is { IsKidMode: true, ActiveProfileId: { } profileId, ActiveProfileHouseholdId: { } profileHousehold }
            && profileHousehold == householdId
            && await db.MemberProfiles.AsNoTracking().AnyAsync(p => p.Id == profileId, cancellationToken))
        {
            return profileId;
        }

        return null;
    }

    private static IResult NoActiveProfile() =>
        AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.NoActiveProfile,
            "No active profile",
            "Only a kid profile can like a recipe. Switch to a profile first.");

    private static IResult RecipeNotFound() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.RecipeNotFound, "Recipe not found");
}

/// <summary>Who likes the given recipes, for the recipe reads.</summary>
internal static class RecipeLikeReads
{
    /// <summary>The profiles of this household that like each recipe, ordered by name. Recipes nobody likes are absent.</summary>
    public static async Task<Dictionary<Guid, ProfileResponse[]>> LikedByAsync(
        AppDbContext db, List<Guid> recipeIds, CancellationToken cancellationToken)
    {
        if (recipeIds.Count == 0)
        {
            return [];
        }

        var rows = await (from like in db.RecipeLikes.AsNoTracking()
                          join profile in db.MemberProfiles.AsNoTracking() on like.MemberProfileId equals profile.Id
                          where recipeIds.Contains(like.RecipeId)
                          select new { like.RecipeId, profile.Id, profile.DisplayName, profile.Avatar }).ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.RecipeId).ToDictionary(
            g => g.Key,
            g => g.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Id)
                .Select(r => new ProfileResponse(r.Id, r.DisplayName, r.Avatar)).ToArray());
    }

    public static bool IsLikedBy(ProfileResponse[] likedBy, Guid? profileId) =>
        profileId is { } id && likedBy.Any(p => p.Id == id);
}
