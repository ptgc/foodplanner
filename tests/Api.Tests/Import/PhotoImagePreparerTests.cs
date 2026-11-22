using Foodplanner.Domain.Imports;
using Foodplanner.Infrastructure.Images;
using NetVips;

namespace Foodplanner.Api.Tests.Import;

/// <summary>Pixel work of the photo import (T-118) with generated images: preparing the photo for Claude and cropping the dish picture.</summary>
public class PhotoImagePreparerTests
{
    private static readonly ImageOptions Limits = new();

    /// <summary>Left half red, right half blue, stored as given; <paramref name="orientation"/> is the EXIF orientation tag.</summary>
    private static byte[] TwoColour(int width, int height, int? orientation = null)
    {
        using var red = Image.Black(width / 2, height, bands: 3).NewFromImage(255, 0, 0);
        using var blue = Image.Black(width - (width / 2), height, bands: 3).NewFromImage(0, 0, 255);
        using var joined = red.Join(blue, Enums.Direction.Horizontal);
        using var tagged = joined.Mutate(m =>
        {
            if (orientation is { } o)
            {
                m.Set(GValue.GIntType, "orientation", o);
            }

            m.Set(GValue.GStrType, "exif-ifd0-Make", "SecretCamera");
        });
        return tagged.JpegsaveBuffer(q: 95);
    }

    private static (double R, double B) Average(byte[] jpeg)
    {
        using var image = Image.NewFromBuffer(jpeg);
        return (image.ExtractBand(0).Avg(), image.ExtractBand(2).Avg());
    }

    [Fact]
    public void Prepare_makes_an_upright_metadata_free_jpeg_of_at_most_2000_px()
    {
        var result = PhotoImagePreparer.Prepare(TwoColour(3000, 1500, orientation: 6), Limits);

        Assert.True(result.Succeeded);
        Assert.Equal((1000, 2000), (result.Photo!.Width, result.Photo.Height)); // turned upright, then shrunk
        using var decoded = Image.NewFromBuffer(result.Photo.Jpeg);
        Assert.False(decoded.Contains("exif-ifd0-Make"));
        Assert.False(decoded.Contains("orientation") && decoded.Get("orientation") is int and not 1);
    }

    [Fact]
    public void Prepare_does_not_enlarge_a_small_photo_and_rejects_what_a_recipe_picture_rejects()
    {
        Assert.Equal((400, 300), Dimensions(PhotoImagePreparer.Prepare(TwoColour(400, 300), Limits)));
        Assert.False(PhotoImagePreparer.Prepare("GIF89a"u8.ToArray(), Limits).Succeeded);
        Assert.False(PhotoImagePreparer.Prepare(TwoColour(400, 300), new ImageOptions { MaxDimension = 100 }).Succeeded);
        Assert.False(PhotoImagePreparer.Prepare(TwoColour(400, 300), new ImageOptions { MaxUploadBytes = 100 }).Succeeded);
    }

    [Fact]
    public void Crop_cuts_the_expected_rectangle_of_the_original()
    {
        var original = TwoColour(1000, 600);

        var left = PhotoImagePreparer.Crop(original, new PictureCrop(0, 0.0, 0.0, 0.5, 1.0, 0))!;
        using (var image = Image.NewFromBuffer(left))
        {
            Assert.Equal((500, 600), (image.Width, image.Height));
        }

        var (red, blue) = Average(left);
        Assert.True(red > 240 && blue < 15);

        var right = PhotoImagePreparer.Crop(original, new PictureCrop(0, 0.5, 0.0, 0.5, 1.0, 0))!;
        var (red2, blue2) = Average(right);
        Assert.True(red2 < 15 && blue2 > 240);
    }

    [Fact]
    public void Crop_rotates_by_the_given_angle_and_the_output_dimensions_swap()
    {
        var original = TwoColour(1000, 600);
        var box = new PictureCrop(0, 0.0, 0.0, 0.5, 1.0, 90);

        using var rotated = Image.NewFromBuffer(PhotoImagePreparer.Crop(original, box)!);
        Assert.Equal((600, 500), (rotated.Width, rotated.Height));
        using var half = Image.NewFromBuffer(PhotoImagePreparer.Crop(original, box with { RotationDegrees = 180 })!);
        Assert.Equal((500, 600), (half.Width, half.Height));
        using var again = Image.NewFromBuffer(PhotoImagePreparer.Crop(original, box with { RotationDegrees = 270 })!);
        Assert.Equal((600, 500), (again.Width, again.Height));
    }

    [Fact]
    public void Crop_applies_the_exif_orientation_first_so_the_box_matches_what_claude_saw()
    {
        // Stored 1200 x 800 with the left half red and orientation 6 (turn 90 degrees clockwise to display):
        // upright it is 800 x 1200 with the red half at the TOP.
        var original = TwoColour(1200, 800, orientation: 6);

        var top = PhotoImagePreparer.Crop(original, new PictureCrop(0, 0.0, 0.0, 1.0, 0.5, 0))!;
        using (var image = Image.NewFromBuffer(top))
        {
            Assert.Equal((800, 600), (image.Width, image.Height));
        }

        var (red, blue) = Average(top);
        Assert.True(red > 240 && blue < 15);
        var (red2, blue2) = Average(PhotoImagePreparer.Crop(original, new PictureCrop(0, 0.0, 0.5, 1.0, 0.5, 0))!);
        Assert.True(red2 < 15 && blue2 > 240);
    }

    [Fact]
    public void A_crop_whose_longer_side_is_under_320_pixels_is_dropped_and_the_output_has_no_metadata()
    {
        var original = TwoColour(1000, 600);
        Assert.Null(PhotoImagePreparer.Crop(original, new PictureCrop(0, 0.0, 0.0, 0.3, 0.4, 0))); // 300 x 240
        Assert.NotNull(PhotoImagePreparer.Crop(original, new PictureCrop(0, 0.0, 0.0, 0.33, 0.4, 0))); // 330 x 240

        var cropped = PhotoImagePreparer.Crop(original, new PictureCrop(0, 0.0, 0.0, 0.8, 0.8, 0))!;
        using var intermediate = Image.NewFromBuffer(cropped);
        Assert.False(intermediate.Contains("exif-ifd0-Make"));

        // Through the recipe picture rules it becomes WebP, still without metadata.
        var processed = RecipeImageProcessor.Process(cropped, Limits);
        Assert.True(processed.Succeeded);
        var full = processed.Images!.Single(i => i.Size == Foodplanner.Domain.Images.RecipeImageSize.Full);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(full.Content[..4]));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(full.Content[8..12]));
        Assert.DoesNotContain("SecretCamera", System.Text.Encoding.ASCII.GetString(full.Content), StringComparison.Ordinal);
    }

    [Fact]
    public void Crop_of_unreadable_bytes_is_null()
    {
        Assert.Null(PhotoImagePreparer.Crop([1, 2, 3], new PictureCrop(0, 0, 0, 1, 1, 0)));
    }

    private static (int, int) Dimensions(PhotoPrepareResult result) => (result.Photo!.Width, result.Photo.Height);
}
