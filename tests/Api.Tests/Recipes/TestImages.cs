using NetVips;

namespace Foodplanner.Api.Tests.Recipes;

/// <summary>Builds real pictures for the upload tests with libvips, including ones carrying metadata that must not survive.</summary>
internal static class TestImages
{
    /// <summary>A plain colour picture. Optional EXIF orientation, camera make, GPS and XMP, all of which a family photo has.</summary>
    public static byte[] Jpeg(int width, int height, int? orientation = null, bool withMetadata = false)
    {
        using var image = Image.Black(width, height, bands: 3).NewFromImage(120, 160, 60);
        using var copy = image.Mutate(m =>
        {
            if (orientation is { } o)
            {
                m.Set(GValue.GIntType, "orientation", o);
            }

            if (withMetadata)
            {
                m.Set(GValue.GStrType, "exif-ifd0-Make", "SecretCamera");
                m.Set(GValue.GStrType, "exif-ifd3-GPSLatitudeRef", "N");
                m.Set(GValue.GStrType, "exif-ifd3-GPSLongitudeRef", "E");
                m.Set(GValue.BlobType, "xmp-data", System.Text.Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"));
            }
        });
        return copy.JpegsaveBuffer(q: 85);
    }

    public static byte[] Png(int width, int height)
    {
        using var image = Image.Black(width, height, bands: 3).NewFromImage(10, 200, 90);
        return image.PngsaveBuffer();
    }

    public static byte[] StillWebp(int width, int height)
    {
        using var image = Image.Black(width, height, bands: 3).NewFromImage(200, 10, 90);
        return image.WebpsaveBuffer();
    }

    /// <summary>A WebP with three frames.</summary>
    public static byte[] AnimatedWebp()
    {
        // Identical frames would be merged into one by the encoder, so each differs.
        var parts = new[] { 10.0, 120.0, 230.0 }.Select(c => Image.Black(64, 64, bands: 3).NewFromImage(c, 255 - c, 40)).ToArray();
        using var frames = Image.Arrayjoin(parts, across: 1);
        using var copy = frames.Mutate(m => m.Set(GValue.GIntType, "page-height", 64));
        return copy.WebpsaveBuffer();
    }

    /// <summary>A valid PNG with an <c>acTL</c> (animation control) chunk before the image data, which makes it an APNG.</summary>
    public static byte[] AnimatedPng()
    {
        var png = Png(16, 16);
        var actl = new byte[] { 0, 0, 0, 8, (byte)'a', (byte)'c', (byte)'T', (byte)'L', 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0 };
        var afterIhdr = 8 + 25; // signature + IHDR chunk (4 + 4 + 13 + 4)
        return [.. png.Take(afterIhdr), .. actl, .. png.Skip(afterIhdr)];
    }

    /// <summary>Starts like a PNG and then is noise.</summary>
    public static byte[] PngHeaderWithGarbage() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Enumerable.Range(0, 2000).Select(i => (byte)(i * 31 % 251))];

    public static byte[] TruncatedJpeg()
    {
        // Noise does not compress, so cutting the file in half really loses image data.
        using var noise = Image.Gaussnoise(800, 600).Cast(Enums.BandFormat.Uchar);
        var jpeg = noise.JpegsaveBuffer();
        return jpeg[..(jpeg.Length / 2)];
    }

    public static byte[] Gif() => System.Text.Encoding.ASCII.GetBytes("GIF89a\u0001\0\u0001\0\0\0\0;");

    public static byte[] TextRenamed() => System.Text.Encoding.UTF8.GetBytes("This is not a picture, only text renamed to photo.jpg.");

    /// <summary>Loads an encoded picture so a test can read its size and metadata.</summary>
    public static Image Decode(byte[] bytes) => Image.NewFromBuffer(bytes);
}
