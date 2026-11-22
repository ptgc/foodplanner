using Foodplanner.Domain.Imports;
using NetVips;

namespace Foodplanner.Infrastructure.Images;

/// <summary>A photo ready to send: a JPEG with the longest side at most <see cref="PhotoImagePreparer.MaxSide"/>, no metadata.</summary>
public sealed record PreparedPhoto(byte[] Jpeg, int Width, int Height);

public sealed record PhotoPrepareResult(PreparedPhoto? Photo, string? Message)
{
    public bool Succeeded => Photo is not null;
}

/// <summary>
/// Pixel work of the photo import (T-118), NetVips like <see cref="RecipeImageProcessor"/>. The photo is validated by the same
/// checks as a recipe picture (<see cref="RecipeImageProcessor.Validate"/>), turned upright by its EXIF orientation, shrunk to
/// at most 2000 px and re-encoded as JPEG without EXIF, GPS or XMP: that is the only form that leaves the server. Nothing is
/// written to disk; the original bytes stay in the request's memory to crop the dish picture at full resolution.
/// </summary>
public static class PhotoImagePreparer
{
    public const int MaxSide = 2000;
    public const int JpegQuality = 85;

    static PhotoImagePreparer()
    {
        Cache.Max = 0;
        Cache.MaxMem = 0;
    }

    public static PhotoPrepareResult Prepare(byte[] bytes, ImageOptions options)
    {
        if (RecipeImageProcessor.Validate(bytes, options) is { } rejected)
        {
            return new PhotoPrepareResult(null, rejected.Message);
        }

        try
        {
            using var resized = Image.ThumbnailBuffer(
                bytes, MaxSide, height: MaxSide, size: Enums.Size.Down, noRotate: false, outputProfile: "srgb", failOn: Enums.FailOn.Warning);
            var jpeg = resized.JpegsaveBuffer(q: JpegQuality, keep: Enums.ForeignKeep.None);
            return new PhotoPrepareResult(new PreparedPhoto(jpeg, resized.Width, resized.Height), null);
        }
        catch (VipsException)
        {
            return new PhotoPrepareResult(null, "The file is not a valid JPEG, PNG or WebP picture.");
        }
    }

    /// <summary>
    /// Crops the dish picture out of the ORIGINAL photo: orientation applied first (so the box matches what Claude saw), then the
    /// box, then the rotation that makes the dish upright. Returns a high-quality JPEG for <see cref="RecipeImageProcessor"/>, or
    /// null when the result is under <see cref="PictureCrop.MinLongSidePixels"/> on its longer side or cannot be read.
    /// </summary>
    public static byte[]? Crop(byte[] original, PictureCrop crop)
    {
        try
        {
            using var oriented = Image.NewFromBuffer(original, access: Enums.Access.Random, failOn: Enums.FailOn.Warning).Autorot();
            var left = Math.Clamp((int)Math.Round(crop.X * oriented.Width), 0, oriented.Width - 1);
            var top = Math.Clamp((int)Math.Round(crop.Y * oriented.Height), 0, oriented.Height - 1);
            var width = Math.Clamp((int)Math.Round(crop.W * oriented.Width), 1, oriented.Width - left);
            var height = Math.Clamp((int)Math.Round(crop.H * oriented.Height), 1, oriented.Height - top);
            using var cropped = oriented.Crop(left, top, width, height);
            using var upright = crop.RotationDegrees switch
            {
                90 => cropped.Rot(Enums.Angle.D90),
                180 => cropped.Rot(Enums.Angle.D180),
                270 => cropped.Rot(Enums.Angle.D270),
                _ => cropped.Copy(),
            };
            if (Math.Max(upright.Width, upright.Height) < PictureCrop.MinLongSidePixels)
            {
                return null;
            }

            return upright.Colourspace(Enums.Interpretation.Srgb).JpegsaveBuffer(q: 92, keep: Enums.ForeignKeep.None);
        }
        catch (VipsException)
        {
            return null;
        }
    }
}
