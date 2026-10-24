using System.Net;
using System.Text;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Images;
using Foodplanner.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Recipes;

/// <summary>
/// Serving, deleting and storing recipe images against a real Azurite. Blobs are written through the storage port
/// (uploads are in RecipeImageUploadTests), then read through the API as different callers.
/// </summary>
public sealed class RecipeImageTests(AuthDatabase db, AzuriteFixture azurite) : IClassFixture<AuthDatabase>
{
    private static readonly Guid Salt = IngredientSeed.All.Single(r => r.Key == "salt").Id;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AuthHost Host() => HouseholdHost.Create(db.ConnectionString, ("ConnectionStrings:Blobs", azurite.ConnectionString));

    private static string RecipesUrl(Guid household, string rest = "") => $"/api/households/{household}/recipes{rest}";

    private static byte[] Bytes(RecipeImageSize size) => Encoding.ASCII.GetBytes($"fake-webp-{size}-{Guid.NewGuid():N}");

    private static async Task<Guid> CreateRecipeAsync(ApiClient client, Guid household)
    {
        using var response = await client.PostAsync(RecipesUrl(household), new
        {
            title = "Bilde " + Guid.NewGuid().ToString("N"),
            servings = 2,
            ingredients = new[] { new { ingredientId = Salt, quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    /// <summary>Stores three blobs the way an upload will and points the recipe at them. Returns the version and the bytes per size.</summary>
    private async Task<(Guid Version, Dictionary<RecipeImageSize, byte[]> Content)> SeedImageAsync(AuthHost host, Guid household, Guid recipe)
    {
        var store = host.Services.GetRequiredService<IImageStore>();
        var version = Guid.NewGuid();
        var content = new Dictionary<RecipeImageSize, byte[]>();
        foreach (var size in RecipeImageSizes.All)
        {
            content[size] = Bytes(size);
            await store.PutAsync(RecipeImageBlobNames.Blob(household, recipe, version, size), content[size], "image/webp", Ct);
        }

        await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "UPDATE Recipes SET ImageVersion = @v WHERE Id = @id", ("@v", version), ("@id", recipe));
        return (version, content);
    }

    private async Task<bool> ExistsAsync(AuthHost host, Guid household, Guid recipe, Guid version, RecipeImageSize size) =>
        await host.Services.GetRequiredService<IImageStore>().OpenReadAsync(RecipeImageBlobNames.Blob(household, recipe, version, size), Ct) is { } stream
        && await DisposeAsync(stream);

    private static async Task<bool> DisposeAsync(Stream stream)
    {
        await stream.DisposeAsync();
        return true;
    }

    private static string ImageUrl(Guid household, Guid recipe, Guid version, string size) =>
        RecipesUrl(household, $"/{recipe}/image/{version:N}/{size}");

    [Fact]
    public async Task Member_reads_each_size_with_immutable_cache_headers_and_an_etag()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var recipe = await CreateRecipeAsync(owner.Client, household);
        var (version, content) = await SeedImageAsync(host, household, recipe);

        foreach (var size in RecipeImageSizes.All)
        {
            var name = RecipeImageSizes.Name(size);
            using var response = await owner.Client.GetAsync(ImageUrl(household, recipe, version, name));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(content[size], await response.Content.ReadAsByteArrayAsync(Ct));
            Assert.True(response.Headers.CacheControl?.Private);
            Assert.Equal(TimeSpan.FromDays(365), response.Headers.CacheControl?.MaxAge);
            Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
            Assert.Equal($"\"{version:N}-{name}\"", response.Headers.ETag?.Tag);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());

            using var revalidated = await owner.Client.GetAsync(
                ImageUrl(household, recipe, version, name), new Dictionary<string, string> { ["If-None-Match"] = $"\"{version:N}-{name}\"" });
            Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
        }
    }

    [Fact]
    public async Task Recipe_responses_carry_the_urls_of_the_three_sizes_and_null_without_an_image()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var bare = await CreateRecipeAsync(owner.Client, household);
        var withImage = await CreateRecipeAsync(owner.Client, household);
        var (version, _) = await SeedImageAsync(host, household, withImage);

        using var none = await owner.Client.GetAsync(RecipesUrl(household, "/" + bare));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await none.JsonAsync()).GetProperty("image").ValueKind);

        using var detail = await owner.Client.GetAsync(RecipesUrl(household, "/" + withImage));
        var image = (await detail.JsonAsync()).GetProperty("image");
        Assert.Equal(ImageUrl(household, withImage, version, "thumb"), image.GetProperty("thumbUrl").GetString());
        Assert.Equal(ImageUrl(household, withImage, version, "card"), image.GetProperty("cardUrl").GetString());
        Assert.Equal(ImageUrl(household, withImage, version, "full"), image.GetProperty("fullUrl").GetString());

        using var list = await owner.Client.GetAsync(RecipesUrl(household));
        var items = (await list.JsonAsync()).GetProperty("items").EnumerateArray().ToList();
        var listed = items.Single(i => i.GetProperty("id").GetGuid() == withImage);
        Assert.Equal(ImageUrl(household, withImage, version, "thumb"), listed.GetProperty("thumbUrl").GetString());
        Assert.Equal(ImageUrl(household, withImage, version, "card"), listed.GetProperty("cardUrl").GetString());
        var unlisted = items.Single(i => i.GetProperty("id").GetGuid() == bare);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, unlisted.GetProperty("thumbUrl").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, unlisted.GetProperty("cardUrl").ValueKind);
    }

    [Fact]
    public async Task Child_reads_but_cannot_delete()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        var recipe = await CreateRecipeAsync(owner.Client, household);
        var (version, _) = await SeedImageAsync(host, household, recipe);

        using var read = await child.Client.GetAsync(ImageUrl(household, recipe, version, "card"));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var delete = await child.Client.DeleteAsync(RecipesUrl(household, $"/{recipe}/image"));
        await delete.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        Assert.True(await ExistsAsync(host, household, recipe, version, RecipeImageSize.Card));
    }

    [Fact]
    public async Task Another_household_cannot_read_or_delete_the_image_even_with_the_exact_url()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var recipe = await CreateRecipeAsync(owner.Client, household);
        var (version, _) = await SeedImageAsync(host, household, recipe);
        var outsider = await host.SignInAsync();
        var otherHousehold = await outsider.Client.CreateHouseholdAsync();

        // Their own member of nothing here: the household route refuses before any lookup.
        using var read = await outsider.Client.GetAsync(ImageUrl(household, recipe, version, "full"));
        await read.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var delete = await outsider.Client.DeleteAsync(RecipesUrl(household, $"/{recipe}/image"));
        await delete.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Their own household route with the foreign recipe id: the tenant filter hides the recipe.
        using var viaOwnRoute = await outsider.Client.GetAsync(ImageUrl(otherHousehold, recipe, version, "full"));
        await viaOwnRoute.AssertProblemAsync(HttpStatusCode.NotFound, "recipe-not-found");
        using var deleteViaOwnRoute = await outsider.Client.DeleteAsync(RecipesUrl(otherHousehold, $"/{recipe}/image"));
        await deleteViaOwnRoute.AssertProblemAsync(HttpStatusCode.NotFound, "recipe-not-found");

        // Signed out.
        using var anonymous = host.CreateClient();
        using var unauthenticated = await anonymous.GetAsync(ImageUrl(household, recipe, version, "full"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        Assert.True(await ExistsAsync(host, household, recipe, version, RecipeImageSize.Full));
    }

    [Theory]
    [InlineData("huge")]
    [InlineData("Card")]
    public async Task Unknown_size_or_stale_version_is_not_found(string size)
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var recipe = await CreateRecipeAsync(owner.Client, household);
        var (version, _) = await SeedImageAsync(host, household, recipe);

        using var badSize = await owner.Client.GetAsync(ImageUrl(household, recipe, version, size));
        Assert.Equal(HttpStatusCode.NotFound, badSize.StatusCode);
        using var staleVersion = await owner.Client.GetAsync(ImageUrl(household, recipe, Guid.NewGuid(), "card"));
        Assert.Equal(HttpStatusCode.NotFound, staleVersion.StatusCode);
    }

    [Fact]
    public async Task Delete_removes_the_blobs_and_the_reference()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var recipe = await CreateRecipeAsync(owner.Client, household);
        var (version, _) = await SeedImageAsync(host, household, recipe);

        using var deleted = await adult.Client.DeleteAsync(RecipesUrl(household, $"/{recipe}/image"));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        foreach (var size in RecipeImageSizes.All)
        {
            Assert.False(await ExistsAsync(host, household, recipe, version, size));
        }

        using var detail = await owner.Client.GetAsync(RecipesUrl(household, "/" + recipe));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await detail.JsonAsync()).GetProperty("image").ValueKind);
        using var gone = await owner.Client.GetAsync(ImageUrl(household, recipe, version, "card"));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var again = await adult.Client.DeleteAsync(RecipesUrl(household, $"/{recipe}/image"));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Deleting_the_recipe_removes_its_blobs()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var recipe = await CreateRecipeAsync(owner.Client, household);
        var (version, _) = await SeedImageAsync(host, household, recipe);

        using var deleted = await owner.Client.DeleteAsync(RecipesUrl(household, "/" + recipe));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        foreach (var size in RecipeImageSizes.All)
        {
            Assert.False(await ExistsAsync(host, household, recipe, version, size));
        }
    }

    [Fact]
    public async Task Store_round_trips_a_blob_and_deletes_only_the_given_prefix()
    {
        await using var host = Host();
        var store = host.Services.GetRequiredService<IImageStore>();
        var household = Guid.NewGuid();
        var recipe = Guid.NewGuid();
        var (v1, v2) = (Guid.NewGuid(), Guid.NewGuid());
        var one = RecipeImageBlobNames.Blob(household, recipe, v1, RecipeImageSize.Card);
        var two = RecipeImageBlobNames.Blob(household, recipe, v2, RecipeImageSize.Card);
        await store.PutAsync(one, [1, 2, 3], "image/webp", Ct);
        await store.PutAsync(two, [4, 5, 6], "image/webp", Ct);

        await using (var stream = (await store.OpenReadAsync(one, Ct))!)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, Ct);
            Assert.Equal<byte>([1, 2, 3], copy.ToArray());
        }

        await store.DeletePrefixAsync(RecipeImageBlobNames.VersionPrefix(household, recipe, v1), Ct);

        Assert.Null(await store.OpenReadAsync(one, Ct));
        await using (var kept = await store.OpenReadAsync(two, Ct))
        {
            Assert.NotNull(kept);
        }

        Assert.Null(await store.OpenReadAsync("no/such/blob.webp", Ct));
        Assert.StartsWith($"{household:N}/{recipe:N}/{v1:N}/", one); // the household id and recipe id are in every blob name
    }
}
