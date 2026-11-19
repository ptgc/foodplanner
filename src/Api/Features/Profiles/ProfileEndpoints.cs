using Foodplanner.Api.Authorization;
using Foodplanner.Api.Features.Auth;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Households;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Profiles;

/// <summary>A kid profile as the picker and the "liked by" rows show it.</summary>
public sealed record ProfileResponse(Guid Id, string DisplayName, string Avatar);

/// <summary>Create or change a kid profile. <c>Avatar</c> is one of the fixed keys (fox, bear, cat, dog, owl, frog, panda, rabbit, lion, penguin, koala, whale).</summary>
public sealed record ProfileRequest(string? DisplayName, string? Avatar);

/// <summary>
/// Kid profiles (T-026). Any member lists them (the picker); Adult or Owner create, change and delete them. Activating a
/// profile locks THIS device's session to it (kid mode): see docs/domain.md and docs/specs/T-026-kid-profiles.md.
/// </summary>
public static class ProfileEndpoints
{
    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapHouseholdScopedGroup();
        var profiles = group.MapGroup("/profiles");

        profiles.MapGet("", ListProfiles)
            .WithName("ListProfiles")
            .Produces<ProfileResponse[]>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        profiles.MapPost("", CreateProfile)
            .WithName("CreateProfile")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces<ProfileResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        profiles.MapPut("/{profileId:guid}", UpdateProfile)
            .WithName("UpdateProfile")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces<ProfileResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        profiles.MapDelete("/{profileId:guid}", DeleteProfile)
            .WithName("DeleteProfile")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        profiles.MapPost("/{profileId:guid}/activate", ActivateProfile)
            .WithName("ActivateProfile")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static ProfileResponse ToResponse(MemberProfile p) => new(p.Id, p.DisplayName, p.Avatar);

    private static async Task<IResult> ListProfiles(AppDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.MemberProfiles.AsNoTracking().ToListAsync(cancellationToken);
        return Results.Ok(rows.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id).Select(ToResponse).ToArray());
    }

    private static async Task<IResult> CreateProfile(ProfileRequest? request, AppDbContext db, CancellationToken cancellationToken)
    {
        if (!TryValidate(request, out var name, out var avatar, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        if (await db.MemberProfiles.AnyAsync(p => p.DisplayName == name, cancellationToken))
        {
            return Duplicate(name);
        }

        if (await db.MemberProfiles.CountAsync(cancellationToken) >= MemberProfile.MaxPerHousehold)
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                AppProblems.ProfileLimit,
                "Too many profiles",
                $"A household can have at most {MemberProfile.MaxPerHousehold} kid profiles.");
        }

        var profile = new MemberProfile(name, avatar);
        db.MemberProfiles.Add(profile);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            return Duplicate(name);
        }

        return Results.Created($"/api/households/{profile.HouseholdId}/profiles", ToResponse(profile));
    }

    private static async Task<IResult> UpdateProfile(Guid profileId, ProfileRequest? request, AppDbContext db, CancellationToken cancellationToken)
    {
        if (!TryValidate(request, out var name, out var avatar, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        var profile = await db.MemberProfiles.FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);
        if (profile is null)
        {
            return NotFound();
        }

        if (await db.MemberProfiles.AnyAsync(p => p.Id != profileId && p.DisplayName == name, cancellationToken))
        {
            return Duplicate(name);
        }

        profile.Change(name, avatar);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            return Duplicate(name);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Conflict();
        }

        return Results.Ok(ToResponse(profile));
    }

    /// <summary>
    /// Deletes the profile with its likes, and clears it from the entries it picked (tracked updates, one transaction), so the
    /// history keeps only the account holder. Allowed while the profile is active on some device: that device stays locked as
    /// Child with no active profile.
    /// </summary>
    private static async Task<IResult> DeleteProfile(Guid profileId, AppDbContext db, CancellationToken cancellationToken)
    {
        var profile = await db.MemberProfiles.FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);
        if (profile is null)
        {
            return NotFound();
        }

        db.RecipeLikes.RemoveRange(await db.RecipeLikes.Where(l => l.MemberProfileId == profileId).ToListAsync(cancellationToken));
        foreach (var entry in await db.MealPlanEntries.Where(e => e.PickedByProfileId == profileId).ToListAsync(cancellationToken))
        {
            entry.ClearPickedByProfile();
        }

        db.MemberProfiles.Remove(profile);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException or DbUpdateException)
        {
            // Gone already, or someone liked or picked as the profile between the reads and the save (foreign key).
            db.ChangeTracker.Clear();
            return await db.MemberProfiles.AnyAsync(p => p.Id == profileId, cancellationToken) ? Conflict() : Results.NoContent();
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Locks this device's session to the profile. An account that is not yet in kid mode needs a PIN to get back out, so
    /// without one the call is refused (409 pin-not-set); a device already in kid mode may switch to another profile freely.
    /// </summary>
    private static async Task<IResult> ActivateProfile(
        Guid householdId, Guid profileId, ICurrentUser user, AppDbContext db, PinService pins, IKidModeSession session,
        CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId)
        {
            return AppProblems.Problem(StatusCodes.Status401Unauthorized, AppProblems.Unauthenticated, "Not signed in");
        }

        // Through the filtered set: another household's profile is "not found".
        if (!await db.MemberProfiles.AsNoTracking().AnyAsync(p => p.Id == profileId, cancellationToken))
        {
            return NotFound();
        }

        if (!user.IsKidMode && !await pins.HasPinAsync(userId, cancellationToken))
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                AppProblems.PinNotSet,
                "No PIN set",
                "Set a PIN first: it is what brings this device back from kid mode.");
        }

        return await session.EnterAsync(userId, profileId, householdId)
            ? Results.NoContent()
            : AppProblems.Problem(StatusCodes.Status401Unauthorized, AppProblems.Unauthenticated, "Not signed in");
    }

    private static bool TryValidate(
        ProfileRequest? request, out string name, out string avatar, out Dictionary<string, string[]> errors)
    {
        errors = [];
        name = string.Empty;
        avatar = string.Empty;
        if (request is null)
        {
            errors["body"] = ["A request body is required."];
            return false;
        }

        var normalized = MemberProfile.NormalizeName(request.DisplayName);
        if (normalized is null)
        {
            errors["displayName"] = [$"The name must be 1 to {MemberProfile.MaxNameLength} characters, without control characters."];
        }
        else
        {
            name = normalized;
        }

        if (!ProfileAvatars.IsValid(request.Avatar))
        {
            errors["avatar"] = [$"The avatar must be one of: {string.Join(", ", ProfileAvatars.All)}."];
        }
        else
        {
            avatar = request.Avatar!;
        }

        return errors.Count == 0;
    }

    private static IResult NotFound() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.ProfileNotFound, "Profile not found");

    private static IResult Conflict() =>
        AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.ProfileConflict,
            "Profile changed",
            "The profile was changed or deleted by someone else. Reload and try again.");

    private static IResult Duplicate(string name) =>
        AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.ProfileExists,
            "Profile already exists",
            $"There is already a profile called '{name}' in this household.");
}
