using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Tests.Recipes;

public sealed class RecipeTests
{
    private static readonly Guid RecipeId = Guid.NewGuid();
    private static readonly Guid IngredientId = Guid.NewGuid();
    private static readonly Guid UnitId = Guid.NewGuid();

    [Fact]
    public void Total_minutes_is_prep_plus_cook_or_null_when_neither_is_set()
    {
        Assert.Null(new Recipe("Pasta", null, 4, null, null).TotalMinutes);
        Assert.Equal(10, new Recipe("Pasta", null, 4, 10, null).TotalMinutes);
        Assert.Equal(25, new Recipe("Pasta", null, 4, null, 25).TotalMinutes);
        Assert.Equal(35, new Recipe("Pasta", null, 4, 10, 25).TotalMinutes);
    }

    [Theory]
    [InlineData("https://www.matprat.no/oppskrifter/x/", "matprat.no")]
    [InlineData("http://Example.COM:8080/r?id=1#a", "example.com")]
    [InlineData("https://www.sub.example.org/", "sub.example.org")]
    public void Source_name_is_derived_from_the_host_without_www(string url, string expected)
    {
        var recipe = new Recipe("Pasta", null, 4, null, null);

        recipe.SetSource(url, null);

        Assert.Equal(url, recipe.SourceUrl);
        Assert.Equal(expected, recipe.SourceName);
        Assert.Equal(expected, recipe.SourceDisplayName);
    }

    [Fact]
    public void Source_name_is_kept_when_given_and_a_name_without_url_is_a_book_source()
    {
        var recipe = new Recipe("Pasta", null, 4, null, null);
        recipe.SetSource("https://example.com/r", "Mormor");
        Assert.Equal("Mormor", recipe.SourceName);

        recipe.SetSource(null, "Kaptein Sabeltann, Terje Formoe");

        Assert.Null(recipe.SourceUrl);
        Assert.Equal("Kaptein Sabeltann, Terje Formoe", recipe.SourceName);
        Assert.Equal("Kaptein Sabeltann, Terje Formoe", recipe.SourceDisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_url_and_no_name_clears_the_source(string? name)
    {
        var recipe = new Recipe("Pasta", null, 4, null, null);
        recipe.SetSource("https://example.com/r", "Mormor");

        recipe.SetSource(null, name);

        Assert.Null(recipe.SourceUrl);
        Assert.Null(recipe.SourceName);
        Assert.Null(recipe.SourceDisplayName);
    }

    [Fact]
    public void A_book_source_name_has_the_same_length_limit()
    {
        var recipe = new Recipe("Pasta", null, 4, null, null);

        recipe.SetSource(null, new string('n', Recipe.MaxSourceNameLength));

        Assert.Throws<ArgumentException>(() => recipe.SetSource(null, new string('n', Recipe.MaxSourceNameLength + 1)));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relative/path")]
    [InlineData("example.com/x")]
    [InlineData("https://")]
    [InlineData("")]
    public void Source_url_must_be_an_absolute_http_or_https_url(string url)
    {
        Assert.False(Recipe.IsValidSourceUrl(url));
        Assert.Throws<ArgumentException>(() => new Recipe("Pasta", null, 4, null, null).SetSource(url, null));
    }

    [Fact]
    public void Source_limits_are_inclusive()
    {
        var recipe = new Recipe("Pasta", null, 4, null, null);
        var longest = "https://example.com/" + new string('a', Recipe.MaxSourceUrlLength - 20);

        recipe.SetSource(longest, new string('n', Recipe.MaxSourceNameLength));

        Assert.Equal(Recipe.MaxSourceUrlLength, recipe.SourceUrl!.Length);
        Assert.Throws<ArgumentException>(() => recipe.SetSource(longest + "a", null));
        Assert.Throws<ArgumentException>(() => recipe.SetSource(longest, new string('n', Recipe.MaxSourceNameLength + 1)));
    }

    [Theory]
    [InlineData("", 4)]
    [InlineData("  ", 4)]
    [InlineData("ok", 0)]
    [InlineData("ok", 101)]
    public void Invalid_title_or_servings_are_refused(string title, int servings) =>
        Assert.ThrowsAny<ArgumentException>(() => new Recipe(title, null, servings, null, null));

    [Fact]
    public void Limits_are_inclusive()
    {
        var recipe = new Recipe(new string('x', Recipe.MaxTitleLength), new string('y', Recipe.MaxDescriptionLength), Recipe.MaxServings, 0, Recipe.MaxMinutes);

        Assert.Equal(Recipe.MaxMinutes, recipe.TotalMinutes);
        Assert.ThrowsAny<ArgumentException>(() => new Recipe(new string('x', Recipe.MaxTitleLength + 1), null, 1, null, null));
        Assert.ThrowsAny<ArgumentException>(() => new Recipe("ok", new string('y', Recipe.MaxDescriptionLength + 1), 1, null, null));
        Assert.ThrowsAny<ArgumentException>(() => new Recipe("ok", null, 1, -1, null));
        Assert.ThrowsAny<ArgumentException>(() => new Recipe("ok", null, 1, null, Recipe.MaxMinutes + 1));
    }

    [Fact]
    public void Update_replaces_the_fields_and_validates_like_the_constructor()
    {
        var recipe = new Recipe("Pasta", null, 4, 1, 2);

        recipe.Update("Lasagne", "Layers", 6, null, 45);

        Assert.Equal(("Lasagne", "Layers", 6, null, 45), (recipe.Title, recipe.Description, recipe.Servings, recipe.PrepMinutes, recipe.CookMinutes));
        Assert.ThrowsAny<ArgumentException>(() => recipe.Update("", null, 6, null, null));
        Assert.Equal("Lasagne", recipe.Title);
    }

    [Fact]
    public void A_step_needs_a_recipe_and_text()
    {
        Assert.Equal("Stir", new RecipeStep(RecipeId, 0, "Stir").Text);
        Assert.Throws<ArgumentException>(() => new RecipeStep(Guid.Empty, 0, "Stir"));
        Assert.Throws<ArgumentException>(() => new RecipeStep(RecipeId, 0, " "));
        Assert.Throws<ArgumentException>(() => new RecipeStep(RecipeId, 0, new string('x', RecipeStep.MaxTextLength + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecipeStep(RecipeId, -1, "Stir"));
    }

    [Fact]
    public void An_ingredient_line_is_structured_and_to_taste_has_no_quantity_and_no_unit()
    {
        var measured = new RecipeIngredient(RecipeId, 0, IngredientId, 1.5m, UnitId, "chopped");
        var toTaste = new RecipeIngredient(RecipeId, 1, IngredientId, null, null, null);

        Assert.Equal((1.5m, UnitId, "chopped"), (measured.Quantity, measured.UnitId, measured.Note));
        Assert.Equal((null, null), (toTaste.Quantity, toTaste.UnitId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100000.01)]
    [InlineData(0.00001)]
    public void Quantity_must_be_positive_bounded_and_at_most_four_decimals(double quantity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecipeIngredient(RecipeId, 0, IngredientId, (decimal)quantity, UnitId, null));

    [Fact]
    public void Quantity_and_unit_go_together_and_ids_are_required()
    {
        Assert.Throws<ArgumentException>(() => new RecipeIngredient(RecipeId, 0, IngredientId, 1m, null, null));
        Assert.Throws<ArgumentException>(() => new RecipeIngredient(RecipeId, 0, IngredientId, null, UnitId, null));
        Assert.Throws<ArgumentException>(() => new RecipeIngredient(RecipeId, 0, Guid.Empty, 1m, UnitId, null));
        Assert.Throws<ArgumentException>(() => new RecipeIngredient(Guid.Empty, 0, IngredientId, 1m, UnitId, null));
        Assert.Throws<ArgumentException>(() => new RecipeIngredient(RecipeId, 0, IngredientId, 1m, UnitId, new string('x', RecipeIngredient.MaxNoteLength + 1)));
        Assert.Equal(100000m, new RecipeIngredient(RecipeId, 0, IngredientId, 100000m, UnitId, null).Quantity);
    }
}
