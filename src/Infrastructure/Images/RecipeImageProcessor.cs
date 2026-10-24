using Foodplanner.Domain.Images;
using NetVips;

namespace Foodplanner.Infrastructure.Images;

/// <summary>Limits for uploaded images. Section <c>Images</c>; every value has a safe default.</summary>
public sealed class ImageOptions
{
    public const string Section = "Images";

    /// <summary>Largest accepted upload, in bytes.</summary>
    public long MaxUploadBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Largest accepted width or height, in pixels.</summary>
    public int MaxDimension { get; set; } = 8000;

    /// <summary>Largest accepted width times height. Bounds the work a decode can take.</summary>
    public long MaxPixels { get; set; } = 40_000_000;

    /// <summary>Lossy WebP quality of the stored sizes (1 to 100).</summary>
    public int WebpQuality { get; set; } = 80;
}

public enum ImageRejection
{
    /// <summary>Not a JPEG, PNG or WebP by its bytes, or the bytes do not decode.</summary>
    NotAnImage,

    /// <summary>More than one frame (animated).</summary>
    Animated,

    /// <summary>Wider, taller or with more pixels than the limits allow.</summary>
    TooLarge,
}

/// <summary>One stored size: WebP bytes and the pixel size of the encoded image.</summary>
public sealed record ProcessedImage(RecipeImageSize Size, byte[] Content, int Width, int Height);

public sealed record ImageProcessingResult(IReadOnlyList<ProcessedImage>? Images, ImageRejection? Rejection, string? Message)
{
    public bool Succeeded => Images is not null;
}

/// <summary>
/// Validates an uploaded picture from its bytes (never the client's content type) and produces the three stored sizes
/// as lossy WebP with libvips (NetVips). The format is decided by the file's magic bytes and only JPEG, PNG and WebP
/// have a loader here. Size and frame count are read from the header, before any pixel is decoded, so a decompression
/// bomb is refused cheaply. The EXIF orientation is applied to the pixels, the picture is converted to sRGB, and then
/// everything but pixels is dropped: EXIF (GPS, camera, owner), XMP, IPTC and the ICC profile.
/// </summary>
public static class RecipeImageProcessor
{
    public const string ContentType = "image/webp";

    static RecipeImageProcessor()
    {
        // Uploads are one-offs: caching operation results would only hold on to memory.
        Cache.Max = 0;
        Cache.MaxMem = 0;
    }

    /// <summary>Reads at most <paramref name="options"/>.MaxUploadBytes + 1 bytes; the caller has already compared the declared length.</summary>
    public static ImageProcessingResult Process(Stream content, ImageOptions options)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        return Process(buffer.ToArray(), options);
    }

    public static ImageProcessingResult Process(byte[] bytes, ImageOptions options)
    {
        if (bytes.LongLength > options.MaxUploadBytes)
        {
            return Reject(ImageRejection.TooLarge, $"The file is larger than {options.MaxUploadBytes / (1024 * 1024)} MB.");
        }

        var format = Sniff(bytes);
        if (format == Format.Unknown)
        {
            return Reject(ImageRejection.NotAnImage, "The file is not a JPEG, PNG or WebP picture.");
        }

        if (format == Format.Png && IsAnimatedPng(bytes))
        {
            return Reject(ImageRejection.Animated, "Animated images are not supported. Upload a still picture.");
        }

        try
        {
            // Header only: with sequential access nothing is decoded until a pixel is asked for.
            using var header = Open(bytes, format);
            if (header.Width > options.MaxDimension || header.Height > options.MaxDimension
                || (long)header.Width * header.Height > options.MaxPixels)
            {
                return Reject(
                    ImageRejection.TooLarge,
                    $"The picture is {header.Width} x {header.Height} pixels. At most {options.MaxDimension} pixels on a side and {options.MaxPixels / 1_000_000} megapixels are accepted.");
            }

            if (header.Contains("n-pages") && header.Get("n-pages") is int pages and > 1)
            {
                return Reject(ImageRejection.Animated, "Animated images are not supported. Upload a still picture.");
            }

            // One full decode with warnings fatal: a truncated or damaged file fails here instead of becoming a half-grey picture.
            _ = header.Avg();

            var results = new List<ProcessedImage>();
            foreach (var size in RecipeImageSizes.All)
            {
                var max = RecipeImageSizes.MaxSide(size);
                // Shrink-on-load, EXIF orientation applied, never enlarged (Size.Down), aspect ratio kept inside max x max.
                using var resized = Image.ThumbnailBuffer(
                    bytes, max, height: max, size: Enums.Size.Down, noRotate: false, outputProfile: "srgb", failOn: Enums.FailOn.Warning);
                var encoded = resized.WebpsaveBuffer(q: options.WebpQuality, keep: Enums.ForeignKeep.None);
                results.Add(new ProcessedImage(size, encoded, resized.Width, resized.Height));
            }

            return new ImageProcessingResult(results, null, null);
        }
        catch (VipsException)
        {
            return Reject(ImageRejection.NotAnImage, "The file is not a valid JPEG, PNG or WebP picture.");
        }
    }

    private enum Format
    {
        Unknown,
        Jpeg,
        Png,
        WebP,
    }

    private static Image Open(byte[] bytes, Format format) => format switch
    {
        Format.Jpeg => Image.JpegloadBuffer(bytes, access: Enums.Access.Sequential, failOn: Enums.FailOn.Warning),
        Format.Png => Image.PngloadBuffer(bytes, access: Enums.Access.Sequential, failOn: Enums.FailOn.Warning),
        _ => Image.WebploadBuffer(bytes, access: Enums.Access.Sequential, failOn: Enums.FailOn.Warning),
    };

    private static Format Sniff(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            return Format.Jpeg;
        }

        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (b.AsSpan().StartsWith(png))
        {
            return Format.Png;
        }

        return b.Length >= 12 && b.AsSpan(0, 4).SequenceEqual("RIFF"u8) && b.AsSpan(8, 4).SequenceEqual("WEBP"u8)
            ? Format.WebP
            : Format.Unknown;
    }

    /// <summary>An APNG has an <c>acTL</c> chunk before the first <c>IDAT</c>. Walks the chunk headers only.</summary>
    private static bool IsAnimatedPng(byte[] b)
    {
        var position = 8;
        while (position + 8 <= b.Length)
        {
            var length = (b[position] << 24) | (b[position + 1] << 16) | (b[position + 2] << 8) | b[position + 3];
            var type = b.AsSpan(position + 4, 4);
            if (type.SequenceEqual("acTL"u8))
            {
                return true;
            }

            if (type.SequenceEqual("IDAT"u8) || length < 0)
            {
                return false;
            }

            position += 12 + length;
        }

        return false;
    }

    private static ImageProcessingResult Reject(ImageRejection rejection, string message) => new(null, rejection, message);
}
