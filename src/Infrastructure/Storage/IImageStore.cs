namespace Foodplanner.Infrastructure.Storage;

/// <summary>
/// The port to blob storage for stored images. Names are plain slash-separated paths such as
/// <c>{householdId}/{recipeId}/{version}/{size}.webp</c>. The caller builds them from ids it has already authorised.
/// </summary>
public interface IImageStore
{
    Task PutAsync(string name, byte[] content, string contentType, CancellationToken cancellationToken);

    /// <summary>The blob's content, or null when it does not exist. The caller disposes the stream.</summary>
    Task<Stream?> OpenReadAsync(string name, CancellationToken cancellationToken);

    /// <summary>Deletes every blob whose name starts with <paramref name="prefix"/> (which must end with a slash).</summary>
    Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken);
}

/// <summary>Blob names for recipe images. The household id comes first so one household's files share a prefix.</summary>
public static class RecipeImageBlobNames
{
    /// <summary>Every blob of one household (T-044: deleted with the household). Ends with a slash, as <see cref="IImageStore.DeletePrefixAsync"/> requires.</summary>
    public static string HouseholdPrefix(Guid householdId) => $"{householdId:N}/";

    public static string RecipePrefix(Guid householdId, Guid recipeId) => $"{householdId:N}/{recipeId:N}/";

    public static string VersionPrefix(Guid householdId, Guid recipeId, Guid version) =>
        $"{RecipePrefix(householdId, recipeId)}{version:N}/";

    public static string Blob(Guid householdId, Guid recipeId, Guid version, Domain.Images.RecipeImageSize size) =>
        $"{VersionPrefix(householdId, recipeId, version)}{Domain.Images.RecipeImageSizes.Name(size)}.webp";
}
