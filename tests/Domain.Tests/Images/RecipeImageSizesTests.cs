using Foodplanner.Domain.Images;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Tests.Images;

public sealed class RecipeImageSizesTests
{
    [Theory]
    [InlineData(4000, 3000, RecipeImageSize.Thumb, 160, 120)]
    [InlineData(4000, 3000, RecipeImageSize.Card, 640, 480)]
    [InlineData(4000, 3000, RecipeImageSize.Full, 1600, 1200)]
    [InlineData(3000, 4000, RecipeImageSize.Card, 480, 640)]
    [InlineData(2000, 1000, RecipeImageSize.Thumb, 160, 80)]
    [InlineData(5000, 100, RecipeImageSize.Card, 640, 13)]
    [InlineData(20000, 1, RecipeImageSize.Thumb, 160, 1)]
    public void Fit_keeps_the_aspect_ratio_and_the_longest_side_at_the_maximum(
        int width, int height, RecipeImageSize size, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), RecipeImageSizes.Fit(width, height, size));

    [Theory]
    [InlineData(100, 50)]
    [InlineData(160, 160)]
    public void Fit_never_enlarges_a_small_image(int width, int height) =>
        Assert.Equal((width, height), RecipeImageSizes.Fit(width, height, RecipeImageSize.Full));

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public void Fit_refuses_an_empty_image(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipeImageSizes.Fit(width, height, RecipeImageSize.Card));

    [Fact]
    public void Sizes_grow_from_thumb_to_full_and_names_round_trip()
    {
        Assert.True(RecipeImageSizes.MaxSide(RecipeImageSize.Thumb) < RecipeImageSizes.MaxSide(RecipeImageSize.Card));
        Assert.True(RecipeImageSizes.MaxSide(RecipeImageSize.Card) < RecipeImageSizes.MaxSide(RecipeImageSize.Full));
        foreach (var size in RecipeImageSizes.All)
        {
            Assert.True(RecipeImageSizes.TryParse(RecipeImageSizes.Name(size), out var parsed));
            Assert.Equal(size, parsed);
        }

        Assert.False(RecipeImageSizes.TryParse("Thumb", out _));
        Assert.False(RecipeImageSizes.TryParse("huge", out _));
        Assert.False(RecipeImageSizes.TryParse(null, out _));
    }

    [Fact]
    public void A_recipe_has_no_image_until_a_version_is_set_and_clearing_removes_it()
    {
        var recipe = new Recipe("Pasta", null, 4, null, null);
        Assert.Null(recipe.ImageVersion);

        var version = Guid.NewGuid();
        recipe.SetImage(version);
        Assert.Equal(version, recipe.ImageVersion);

        recipe.ClearImage();
        Assert.Null(recipe.ImageVersion);
        Assert.Throws<ArgumentException>(() => recipe.SetImage(Guid.Empty));
    }
}
