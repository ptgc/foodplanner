namespace Foodplanner.Domain.Images;

/// <summary>The fixed sizes a recipe image is stored in (ADR 0007: no on-the-fly resizing).</summary>
public enum RecipeImageSize
{
    Thumb,
    Card,
    Full,
}

/// <summary>Pure sizing rules for <see cref="RecipeImageSize"/>.</summary>
public static class RecipeImageSizes
{
    public static IReadOnlyList<RecipeImageSize> All { get; } = [RecipeImageSize.Thumb, RecipeImageSize.Card, RecipeImageSize.Full];

    /// <summary>The longest side, in pixels, an image of this size may have.</summary>
    public static int MaxSide(RecipeImageSize size) => size switch
    {
        RecipeImageSize.Thumb => 160,
        RecipeImageSize.Card => 640,
        RecipeImageSize.Full => 1600,
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    /// <summary>The lower-case name used in URLs and blob names.</summary>
    public static string Name(RecipeImageSize size) => size switch
    {
        RecipeImageSize.Thumb => "thumb",
        RecipeImageSize.Card => "card",
        RecipeImageSize.Full => "full",
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    public static bool TryParse(string? name, out RecipeImageSize size)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(Name(candidate), name, StringComparison.Ordinal))
            {
                size = candidate;
                return true;
            }
        }

        size = default;
        return false;
    }

    /// <summary>
    /// The pixel size of an image after fitting it inside a square of <see cref="MaxSide"/>: aspect ratio kept, never
    /// enlarged, each side at least 1.
    /// </summary>
    public static (int Width, int Height) Fit(int width, int height, RecipeImageSize size)
    {
        if (width < 1 || height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "An image has at least one pixel on each side.");
        }

        var max = MaxSide(size);
        if (width <= max && height <= max)
        {
            return (width, height);
        }

        var scale = (double)max / Math.Max(width, height);
        return (Math.Clamp((int)Math.Round(width * scale), 1, max), Math.Clamp((int)Math.Round(height * scale), 1, max));
    }
}
