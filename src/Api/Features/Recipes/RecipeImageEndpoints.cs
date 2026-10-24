using Foodplanner.Api.Authorization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Images;
using Foodplanner.Domain.Recipes;
using Foodplanner.Infrastructure.Images;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Foodplanner.Api.Features.Recipes;

/// <summary>Where a recipe's three stored sizes can be fetched. Same-origin, relative URLs under the household route.</summary>
public sealed record RecipeImageUrlsResponse(string ThumbUrl, string CardUrl, string FullUrl);

public static class RecipeImageUrls
{
    /// <summary>The URL of one size, or null when the recipe has no image. The version is part of the path, so a URL never changes content.</summary>
    public static string? For(Guid householdId, Guid recipeId, Guid? version, RecipeImageSize size) =>
        version is { } v ? $"/api/households/{householdId}/recipes/{recipeId}/image/{v:N}/{RecipeImageSizes.Name(size)}" : null;

    public static RecipeImageUrlsResponse? All(Recipe recipe) =>
        recipe.ImageVersion is { } version
            ? new RecipeImageUrlsResponse(
                For(recipe.HouseholdId, recipe.Id, version, RecipeImageSize.Thumb)!,
                For(recipe.HouseholdId, recipe.Id, version, RecipeImageSize.Card)!,
                For(recipe.HouseholdId, recipe.Id, version, RecipeImageSize.Full)!)
            : null;
}

/// <summary>The multipart body of the upload (documentation of the contract; the endpoint binds the part itself).</summary>
public sealed record RecipeImageUpload(IFormFile File);

/// <summary>
/// Recipe images (T-022). Files live in blob storage under <c>{householdId}/{recipeId}/{version}/{size}.webp</c> and are
/// served only through the API, under the household route: the recipe is looked up through the tenant filter first, and
/// the blob name is built from that row's own ids, never from the URL. Any member reads; an Adult or Owner deletes.
/// </summary>
public static class RecipeImageEndpoints
{
    public const string ContentType = "image/webp";

    public static IEndpointRouteBuilder MapRecipeImageEndpoints(this IEndpointRouteBuilder api)
    {
        var image = api.MapHouseholdScopedGroup().MapGroup("/recipes/{recipeId:guid}/image");

        // Multipart form with one part named "file". The antiforgery token is validated by the /api root group (header),
        // so the framework's own form antiforgery check is switched off. The body is capped by Kestrel at the limit
        // plus 1 MB of multipart overhead, before the handler reads anything.
        var uploadLimit = api.ServiceProvider.GetRequiredService<IOptions<ImageOptions>>().Value.MaxUploadBytes + 1024 * 1024;
        image.MapPut("", UploadImage)
            .RequireAuthorization(Policies.HouseholdAdult)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(uploadLimit))
            .Accepts<RecipeImageUpload>("multipart/form-data")
            .WithName("UploadRecipeImage")
            .Produces<RecipeImageUrlsResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        image.MapGet("/{version:guid}/{size}", GetImage)
            .WithName("GetRecipeImage")
            .Produces(StatusCodes.Status200OK, typeof(byte[]), ContentType)
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        image.MapDelete("", DeleteImage)
            .RequireAuthorization(Policies.HouseholdAdult)
            .WithName("DeleteRecipeImage")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    /// <summary>
    /// Validates by content, resizes to the three sizes, stores them under a new version, points the recipe at it and then
    /// deletes the previous version's files. Nothing is stored for a rejected file.
    /// </summary>
    private static async Task<IResult> UploadImage(
        Guid householdId, Guid recipeId, IFormFile? file, AppDbContext db, IImageStore images, IOptions<ImageOptions> options,
        ILogger<Recipe> logger, CancellationToken cancellationToken)
    {
        var limits = options.Value;
        if (file is null || file.Length == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["A picture is required in the form part named 'file'."] });
        }

        if (file.Length > limits.MaxUploadBytes)
        {
            return AppProblems.Problem(
                StatusCodes.Status413PayloadTooLarge,
                AppProblems.ImageTooLarge,
                "Picture too large",
                $"The picture can be at most {limits.MaxUploadBytes / (1024 * 1024)} MB.");
        }

        var recipe = await db.Recipes.FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken);
        if (recipe is null)
        {
            return NotFound();
        }

        await using var upload = file.OpenReadStream();
        var processed = await Task.Run(() => RecipeImageProcessor.Process(upload, limits), cancellationToken);
        if (!processed.Succeeded)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [processed.Message!] });
        }

        var previous = recipe.ImageVersion;
        var version = Guid.NewGuid();
        var prefix = RecipeImageBlobNames.VersionPrefix(householdId, recipeId, version);
        try
        {
            foreach (var image in processed.Images!)
            {
                await images.PutAsync(RecipeImageBlobNames.Blob(householdId, recipeId, version, image.Size), image.Content, RecipeImageProcessor.ContentType, cancellationToken);
            }

            recipe.SetImage(version);
            if (await RecipeEndpoints.TrySaveAsync(db, cancellationToken) is { } conflict)
            {
                await TryDeleteBlobsAsync(images, logger, prefix, CancellationToken.None);
                return conflict;
            }
        }
        catch
        {
            await TryDeleteBlobsAsync(images, logger, prefix, CancellationToken.None);
            throw;
        }

        if (previous is { } old)
        {
            await TryDeleteBlobsAsync(images, logger, RecipeImageBlobNames.VersionPrefix(householdId, recipeId, old), cancellationToken);
        }

        return Results.Ok(RecipeImageUrls.All(recipe));
    }

    private static async Task<IResult> GetImage(
        Guid householdId, Guid recipeId, Guid version, string size, HttpContext http, AppDbContext db, IImageStore images,
        CancellationToken cancellationToken)
    {
        if (!RecipeImageSizes.TryParse(size, out var parsed))
        {
            return NotFound();
        }

        var current = await db.Recipes.AsNoTracking().Where(r => r.Id == recipeId).Select(r => r.ImageVersion).FirstOrDefaultAsync(cancellationToken);
        if (current != version)
        {
            return NotFound();
        }

        // The version in the path makes the content immutable. "private" because the response is for a signed-in member.
        var etag = new EntityTagHeaderValue($"\"{version:N}-{size}\"");
        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        http.Response.Headers.ETag = etag.ToString();
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (http.Request.Headers.IfNoneMatch.Any(value => value == etag.ToString() || value == "*"))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var stream = await images.OpenReadAsync(RecipeImageBlobNames.Blob(householdId, recipeId, version, parsed), cancellationToken);
        return stream is null ? NotFound() : Results.Stream(stream, ContentType);
    }

    private static async Task<IResult> DeleteImage(
        Guid householdId, Guid recipeId, AppDbContext db, IImageStore images, ILogger<Recipe> logger, CancellationToken cancellationToken)
    {
        var recipe = await db.Recipes.FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken);
        if (recipe?.ImageVersion is not { } version)
        {
            return NotFound();
        }

        recipe.ClearImage();
        if (await RecipeEndpoints.TrySaveAsync(db, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        await TryDeleteBlobsAsync(images, logger, RecipeImageBlobNames.VersionPrefix(householdId, recipeId, version), cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// Removes blobs after the database no longer points at them. A storage failure must not fail the request (the
    /// row is already correct); the files are then orphans, which are invisible and harmless, and it is logged.
    /// </summary>
    internal static async Task TryDeleteBlobsAsync(IImageStore images, ILogger logger, string prefix, CancellationToken cancellationToken)
    {
        try
        {
            await images.DeletePrefixAsync(prefix, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not delete image blobs under {Prefix}; they are orphaned.", prefix);
        }
    }

    private static IResult NotFound() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.RecipeNotFound, "Image not found");
}
