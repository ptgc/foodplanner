using System.Text.Json;
using Foodplanner.Domain.Imports;
using Foodplanner.Infrastructure.Images;

namespace Foodplanner.Infrastructure.Import.Ai;

/// <summary>
/// What leaves the server for a photo import (T-118, Q-036): the prepared images, the optional book name the person typed and the
/// unit codes (global data). No household, user, recipe or ingredient data, no catalog.
/// </summary>
public sealed record PhotoReadRequest(IReadOnlyList<PreparedPhoto> Photos, string? SourceName, IReadOnlyList<string> UnitCodes);

public enum PhotoReadOutcome
{
    /// <summary>A recipe was read; <see cref="PhotoReadResult.Recipe"/> is set.</summary>
    Read,
    NotARecipe,
    Unreadable,
    Refused,
    MaxTokens,
    Timeout,
    Failed,
}

/// <summary>The typed result of one call. Tokens are null when the API gave no usage. Never carries photo, page text or upstream bodies.</summary>
public sealed record PhotoReadResult(PhotoReadOutcome Outcome, PhotoRecipe? Recipe, int? InputTokens, int? OutputTokens, string? StopReason);

/// <summary>
/// Claude reading recipe photos (T-118). One call per import; the implementation never throws for an API problem, it returns the
/// outcome. Tests use a fake, <c>verify.sh</c> never calls the real API.
/// </summary>
public interface IRecipePhotoReader
{
    Task<PhotoReadResult> ReadAsync(PhotoReadRequest request, CancellationToken cancellationToken);
}

/// <summary>The client could not be built, so nothing was sent to Claude and the reservation is given back. Carries no photo or text.</summary>
public sealed class PhotoReaderNotReadyException(Exception inner) : Exception("The photo reader could not be prepared.", inner);

/// <summary>The skill file (system prompt) and the answer schema, embedded resources (T-118).</summary>
public static class RecipePhotoSkill
{
    private static readonly Lazy<string> TextValue = new(() => Read("Foodplanner.Skills.recipe-from-photo.md"));
    private static readonly Lazy<string> SchemaValue = new(() => Read("Foodplanner.Skills.recipe-from-photo.schema.json"));

    public static string Text => TextValue.Value;

    public static string SchemaJson => SchemaValue.Value;

    /// <summary>The integer after <c>Version:</c> on the first line.</summary>
    public static int Version
    {
        get
        {
            var first = Text.Split('\n', 2)[0].Trim();
            return first.StartsWith("Version:", StringComparison.Ordinal) && int.TryParse(first["Version:".Length..].Trim(), out var v)
                ? v
                : throw new InvalidOperationException("The skill file must start with 'Version: N'.");
        }
    }

    public static JsonElement Schema => JsonDocument.Parse(SchemaJson).RootElement.Clone();

    private static string Read(string name)
    {
        using var stream = typeof(RecipePhotoSkill).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
