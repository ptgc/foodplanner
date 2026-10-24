using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Images;
using Foodplanner.Infrastructure.Images;
using Foodplanner.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Recipes;

/// <summary>Upload, validation, resizing and replacing recipe images end to end against Azurite and the real libvips.</summary>
public sealed class RecipeImageUploadTests(AuthDatabase db, AzuriteFixture azurite) : IClassFixture<AuthDatabase>
{
    private static readonly Guid Salt = IngredientSeed.All.Single(r => r.Key == "salt").Id;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AuthHost Host(params (string Key, string Value)[] extra) =>
        HouseholdHost.Create(db.ConnectionString, [("ConnectionStrings:Blobs", azurite.ConnectionString), .. extra]);

    private static string RecipesUrl(Guid household, string rest = "") => $"/api/households/{household}/recipes{rest}";

    private static string ImageUrl(Guid household, Guid recipe) => RecipesUrl(household, $"/{recipe}/image");

    private static async Task<Guid> CreateRecipeAsync(ApiClient client, Guid household)
    {
        using var response = await client.PostAsync(RecipesUrl(household), new
        {
            title = "Opplast " + Guid.NewGuid().ToString("N"),
            servings = 2,
            ingredients = new[] { new { ingredientId = Salt, quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<(AuthHost Host, SignedInUser Owner, Guid Household, Guid Recipe)> SetupAsync(AuthHost host)
    {
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        return (host, owner, household, await CreateRecipeAsync(owner.Client, household));
    }

    private static async Task<JsonElement> UploadOkAsync(ApiClient client, Guid household, Guid recipe, byte[] content, string fileName = "photo.jpg")
    {
        using var response = await client.PutFileAsync(ImageUrl(household, recipe), content, fileName);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static async Task<byte[]> FetchAsync(ApiClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    private static async Task<bool> BlobExistsAsync(AuthHost host, Guid household, Guid recipe, Guid version, RecipeImageSize size)
    {
        await using var stream = await host.Services.GetRequiredService<IImageStore>()
            .OpenReadAsync(RecipeImageBlobNames.Blob(household, recipe, version, size), Ct);
        return stream is not null;
    }

    private static Guid VersionOf(JsonElement urls) =>
        Guid.Parse(urls.GetProperty("cardUrl").GetString()!.Split('/')[^2]);

    private static async Task AssertNoImageAsync(ApiClient client, Guid household, Guid recipe)
    {
        using var detail = await client.GetAsync(RecipesUrl(household, "/" + recipe));
        Assert.Equal(JsonValueKind.Null, (await detail.JsonAsync()).GetProperty("image").ValueKind);
    }

    [Fact]
    public async Task Adult_uploads_a_photo_and_three_webp_sizes_are_produced_and_served_with_correct_dimensions()
    {
        await using var host = Host();
        var (_, owner, household, recipe) = await SetupAsync(host);
        var adult = await host.JoinAsync(owner, household, "Adult");

        var urls = await UploadOkAsync(adult.Client, household, recipe, TestImages.Jpeg(2000, 1000));

        var expected = new Dictionary<string, (int W, int H)> { ["thumbUrl"] = (160, 80), ["cardUrl"] = (640, 320), ["fullUrl"] = (1600, 800) };
        foreach (var (property, size) in expected)
        {
            using var picture = TestImages.Decode(await FetchAsync(owner.Client, urls.GetProperty(property).GetString()!));
            Assert.Equal(size, (picture.Width, picture.Height));
        }

        // The recipe now lists and details the same URLs.
        using var detail = await owner.Client.GetAsync(RecipesUrl(household, "/" + recipe));
        Assert.Equal(urls.GetRawText(), (await detail.JsonAsync()).GetProperty("image").GetRawText());
        var version = VersionOf(urls);
        foreach (var size in RecipeImageSizes.All)
        {
            Assert.True(await BlobExistsAsync(host, household, recipe, version, size));
        }
    }

    [Fact]
    public async Task A_small_picture_is_never_enlarged()
    {
        await using var host = Host();
        var (_, owner, household, recipe) = await SetupAsync(host);

        var urls = await UploadOkAsync(owner.Client, household, recipe, TestImages.Png(100, 50), "small.png");

        foreach (var property in new[] { "thumbUrl", "cardUrl", "fullUrl" })
        {
            using var picture = TestImages.Decode(await FetchAsync(owner.Client, urls.GetProperty(property).GetString()!));
            Assert.Equal((100, 50), (picture.Width, picture.Height));
        }
    }

    [Fact]
    public async Task Exif_gps_and_xmp_are_stripped_and_the_orientation_is_applied_to_the_pixels()
    {
        await using var host = Host();
        var (_, owner, household, recipe) = await SetupAsync(host);
        var original = TestImages.Jpeg(800, 400, orientation: 6, withMetadata: true);
        using (var source = TestImages.Decode(original))
        {
            // The test picture really carries what must go.
            Assert.True(source.Contains("exif-ifd0-Make"));
            Assert.True(source.Contains("exif-ifd3-GPSLatitudeRef"));
            Assert.True(source.Contains("xmp-data"));
        }

        var urls = await UploadOkAsync(owner.Client, household, recipe, original);

        foreach (var property in new[] { "thumbUrl", "cardUrl", "fullUrl" })
        {
            var bytes = await FetchAsync(owner.Client, urls.GetProperty(property).GetString()!);
            using var picture = TestImages.Decode(bytes);
            Assert.False(picture.Contains("exif-data"));
            Assert.False(picture.Contains("exif-ifd0-Make"));
            Assert.False(picture.Contains("exif-ifd3-GPSLatitudeRef"));
            Assert.False(picture.Contains("xmp-data"));
            Assert.False(picture.Contains("icc-profile-data"));
            Assert.False(picture.Contains("iptc-data"));
            Assert.True(picture.Height > picture.Width, "orientation 6 turns a landscape file into a portrait picture");
            Assert.DoesNotContain("SecretCamera", System.Text.Encoding.Latin1.GetString(bytes));
            Assert.DoesNotContain("GPS", System.Text.Encoding.Latin1.GetString(bytes));
            Assert.DoesNotContain("xmpmeta", System.Text.Encoding.Latin1.GetString(bytes));
        }
    }

    [Fact]
    public async Task Replacing_the_picture_uses_a_new_version_and_deletes_the_old_versions_blobs()
    {
        await using var host = Host();
        var (_, owner, household, recipe) = await SetupAsync(host);
        var first = VersionOf(await UploadOkAsync(owner.Client, household, recipe, TestImages.Jpeg(900, 600)));

        var second = await UploadOkAsync(owner.Client, household, recipe, TestImages.Png(500, 500), "second.png");

        var secondVersion = VersionOf(second);
        Assert.NotEqual(first, secondVersion);
        foreach (var size in RecipeImageSizes.All)
        {
            Assert.False(await BlobExistsAsync(host, household, recipe, first, size));
            Assert.True(await BlobExistsAsync(host, household, recipe, secondVersion, size));
        }

        using var oldUrl = await owner.Client.GetAsync($"{ImageUrl(household, recipe)}/{first:N}/card");
        Assert.Equal(HttpStatusCode.NotFound, oldUrl.StatusCode);
        using var newPicture = TestImages.Decode(await FetchAsync(owner.Client, second.GetProperty("cardUrl").GetString()!));
        Assert.Equal((500, 500), (newPicture.Width, newPicture.Height));
    }

    [Fact]
    public async Task Webp_is_accepted()
    {
        await using var host = Host();
        var (_, owner, household, recipe) = await SetupAsync(host);

        var urls = await UploadOkAsync(owner.Client, household, recipe, TestImages.StillWebp(700, 300), "photo.webp");

        using var picture = TestImages.Decode(await FetchAsync(owner.Client, urls.GetProperty("cardUrl").GetString()!));
        Assert.Equal((640, 274), (picture.Width, picture.Height));
    }

    public static TheoryData<string> Rejected => new()
    {
        "text-renamed-jpg",
        "png-header-with-garbage",
        "truncated-jpeg",
        "gif",
        "animated-webp",
        "animated-png",
    };

    private static byte[] Bad(string kind) => kind switch
    {
        "text-renamed-jpg" => TestImages.TextRenamed(),
        "png-header-with-garbage" => TestImages.PngHeaderWithGarbage(),
        "truncated-jpeg" => TestImages.TruncatedJpeg(),
        "gif" => TestImages.Gif(),
        "animated-webp" => TestImages.AnimatedWebp(),
        "animated-png" => TestImages.AnimatedPng(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public async Task Non_images_corrupt_and_animated_files_are_rejected_with_400_and_nothing_is_stored(string kind)
    {
        await using var host = Host();
        var (_, owner, household, recipe) = await SetupAsync(host);

        // The client says image/jpeg and the name says .jpg; neither is trusted.
        using var response = await owner.Client.PutFileAsync(ImageUrl(household, recipe), Bad(kind), "photo.jpg", "image/jpeg");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("file", out _));
        await AssertNoImageAsync(owner.Client, household, recipe);
    }

    [Fact]
    public async Task Too_many_bytes_is_413_and_a_rejected_replacement_keeps_the_current_picture()
    {
        await using var host = Host(("Images:MaxUploadBytes", "40000"));
        var (_, owner, household, recipe) = await SetupAsync(host);
        var urls = await UploadOkAsync(owner.Client, household, recipe, TestImages.Jpeg(100, 100));
        var version = VersionOf(urls);

        var big = new byte[40_001];
        big[0] = 0xFF;
        big[1] = 0xD8;
        big[2] = 0xFF;
        using var response = await owner.Client.PutFileAsync(ImageUrl(household, recipe), big);

        await response.AssertProblemAsync(HttpStatusCode.RequestEntityTooLarge, "image-too-large");
        using var detail = await owner.Client.GetAsync(RecipesUrl(household, "/" + recipe));
        Assert.Equal(urls.GetRawText(), (await detail.JsonAsync()).GetProperty("image").GetRawText());
        Assert.True(await BlobExistsAsync(host, household, recipe, version, RecipeImageSize.Card));
    }

    [Fact]
    public async Task Ten_megabytes_is_the_default_limit()
    {
        await using var host = Host();
        var (_, owner, household, recipe) = await SetupAsync(host);

        using var response = await owner.Client.PutFileAsync(ImageUrl(household, recipe), new byte[10 * 1024 * 1024 + 1]);

        await response.AssertProblemAsync(HttpStatusCode.RequestEntityTooLarge, "image-too-large");
    }

    [Theory]
    [InlineData(8001, 8)]
    [InlineData(8, 8001)]
    public async Task Too_large_dimensions_are_rejected_before_the_pixels_are_decoded(int width, int height)
    {
        await using var host = Host();
        var (_, owner, household, recipe) = await SetupAsync(host);

        using var response = await owner.Client.PutFileAsync(ImageUrl(household, recipe), TestImages.Png(width, height), "wide.png", "image/png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var message = (await response.JsonAsync()).GetProperty("errors").GetProperty("file")[0].GetString();
        Assert.Contains("pixels", message);
        await AssertNoImageAsync(owner.Client, household, recipe);
    }

    [Fact]
    public async Task Too_many_megapixels_are_rejected_by_the_configured_limit()
    {
        await using var host = Host(("Images:MaxPixels", "10000"));
        var (_, owner, household, recipe) = await SetupAsync(host);

        using var response = await owner.Client.PutFileAsync(ImageUrl(household, recipe), TestImages.Png(200, 200), "big.png", "image/png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_request_without_the_file_part_is_a_validation_error()
    {
        await using var host = Host();
        var (_, owner, household, recipe) = await SetupAsync(host);

        using var response = await owner.Client.PutFileAsync(ImageUrl(household, recipe), TestImages.Jpeg(50, 50), partName: "picture");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("file", out _));
    }

    [Fact]
    public async Task Child_cannot_upload()
    {
        await using var host = Host();
        var (_, owner, household, recipe) = await SetupAsync(host);
        var child = await host.JoinAsync(owner, household, "Child");

        using var response = await child.Client.PutFileAsync(ImageUrl(household, recipe), TestImages.Jpeg(50, 50));

        await response.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await AssertNoImageAsync(owner.Client, household, recipe);
    }

    [Fact]
    public async Task Another_household_cannot_upload_to_the_recipe()
    {
        await using var host = Host();
        var (_, owner, household, recipe) = await SetupAsync(host);
        var outsider = await host.SignInAsync();
        var otherHousehold = await outsider.Client.CreateHouseholdAsync();

        using var viaForeignRoute = await outsider.Client.PutFileAsync(ImageUrl(household, recipe), TestImages.Jpeg(50, 50));
        await viaForeignRoute.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Own household route, foreign recipe id: the tenant filter hides the recipe, and nothing is stored.
        using var viaOwnRoute = await outsider.Client.PutFileAsync(ImageUrl(otherHousehold, recipe), TestImages.Jpeg(50, 50));
        await viaOwnRoute.AssertProblemAsync(HttpStatusCode.NotFound, "recipe-not-found");

        await AssertNoImageAsync(owner.Client, household, recipe);
    }

    [Fact]
    public async Task Upload_needs_the_antiforgery_token()
    {
        await using var host = Host();
        var (_, owner, household, recipe) = await SetupAsync(host);

        using var response = await owner.Client.PutFileAsync(ImageUrl(household, recipe), TestImages.Jpeg(50, 50), antiforgery: false);

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "antiforgery-invalid");
        await AssertNoImageAsync(owner.Client, household, recipe);
    }
}
