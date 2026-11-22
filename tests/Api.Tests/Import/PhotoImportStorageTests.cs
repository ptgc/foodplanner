using System.Net;
using System.Text;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Profiles;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Imports;
using Foodplanner.Infrastructure.Import.Ai;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foodplanner.Api.Tests.Import;

/// <summary>Recipe from a photo (T-118): what is stored, what is sent to Claude, and the dish picture. Claude is always a fake.</summary>
public sealed class PhotoImportStorageTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(Guid household, string rest = "") => $"/api/households/{household}/imports{rest}";

    [Fact]
    public async Task PhotoImport_photos_not_stored()
    {
        var logs = new Profiles.CapturingLoggerProvider();
        var store = new MemoryImageStore();
        await using var host = PhotoHost.Create(
            db.ConnectionString, FakePhotoReader.Answering(PhotoCanned.Recipe(PhotoCanned.Box())), store,
            services => services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs)),
            ("Logging:LogLevel:Default", "Trace"));
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);
        var photo = PhotoFiles.Jpeg();
        Assert.Contains(PhotoFiles.Marker, Encoding.ASCII.GetString(photo), StringComparison.Ordinal); // the marker is really in the file

        using var form = PhotoFiles.Form([photo, PhotoFiles.Jpeg()]);
        using var response = await PhotoHost.PostFormAsync(owner.Client, household, form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var (_, second) = await PhotoHost.ImportOkAsync(owner.Client, household); // a second run, to be sure
        Assert.NotEqual(Guid.Empty, second);

        // Every string and binary column of every table.
        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync(Ct);
        var columns = new List<(string Table, string Column, string Type)>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                "SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE DATA_TYPE IN ('nvarchar','varchar','nchar','char','text','ntext','varbinary','binary')";
            await using var reader = await query.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        Assert.NotEmpty(columns);
        foreach (var (table, column, type) in columns)
        {
            await using var scan = connection.CreateCommand();
            scan.CommandText = type.Contains("binary", StringComparison.Ordinal)
                ? $"SELECT COUNT(*) FROM [{table}] WHERE CHARINDEX(CAST(@marker AS varbinary(max)), [{column}]) > 0"
                : $"SELECT COUNT(*) FROM [{table}] WHERE CAST([{column}] AS nvarchar(max)) LIKE '%' + @marker + '%'";
            scan.Parameters.AddWithValue("@marker", PhotoFiles.Marker);
            Assert.True((int)(await scan.ExecuteScalarAsync(Ct))! == 0, $"{table}.{column} holds the photo marker");
        }

        // Every blob: only the crops, none holds the uploaded bytes.
        Assert.NotEmpty(store.Blobs);
        Assert.All(store.Blobs.Keys, name => Assert.EndsWith("/picture.webp", name, StringComparison.Ordinal));
        Assert.All(store.Blobs.Values, bytes => Assert.DoesNotContain(PhotoFiles.Marker, Encoding.ASCII.GetString(bytes), StringComparison.Ordinal));

        // The logs hold neither the marker nor the page text.
        Assert.NotEmpty(logs.Messages);
        Assert.All(logs.Messages, message =>
        {
            Assert.DoesNotContain(PhotoFiles.Marker, message, StringComparison.Ordinal);
            Assert.DoesNotContain("PAGETEXT-MARKER", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Bomba torsk", message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PhotoImport_request_contents()
    {
        var reader = FakePhotoReader.Answering();
        await using var host = PhotoHost.Create(db.ConnectionString, reader);
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);

        // A large photo with an EXIF orientation of 6 (rotated 90): the server turns it upright and shrinks it.
        var big = PhotoFiles.Jpeg(3000, 1500, orientation: 6);
        using var form = PhotoFiles.Form([big, PhotoFiles.Jpeg(800, 600)], "Min bok");
        using var response = await PhotoHost.PostFormAsync(owner.Client, household, form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var request = Assert.Single(reader.Requests);
        Assert.Equal(2, request.Photos.Count);
        Assert.Equal("Min bok", request.SourceName);
        Assert.Equal(UnitSeed.All.Select(u => u.Code).Order(StringComparer.Ordinal), request.UnitCodes);
        foreach (var photo in request.Photos)
        {
            Assert.Equal([0xFF, 0xD8, 0xFF], photo.Jpeg[..3]);
            Assert.True(Math.Max(photo.Width, photo.Height) <= 2000);
            using var decoded = NetVips.Image.NewFromBuffer(photo.Jpeg);
            Assert.Equal(photo.Width, decoded.Width);
            Assert.False(decoded.Contains("exif-ifd0-Make"));
            Assert.False(decoded.Contains("exif-ifd3-GPSLatitudeRef"));
            Assert.DoesNotContain(PhotoFiles.Marker, Encoding.ASCII.GetString(photo.Jpeg), StringComparison.Ordinal);
        }

        // Orientation 6 on a 3000x1500 picture: upright it is 1500 wide and 3000 high, shrunk to 1000x2000.
        Assert.Equal((1000, 2000), (request.Photos[0].Width, request.Photos[0].Height));

        // Nothing about the household, the user or the catalog is in the request.
        var everything = string.Join('|', [request.SourceName, .. request.UnitCodes]);
        Assert.DoesNotContain(household.ToString(), everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(owner.User.Id.ToString(), everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(owner.User.Email, everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Torsk", everything, StringComparison.Ordinal);
    }

    // ---- The dish picture ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PhotoImport_picture_flow()
    {
        var store = new MemoryImageStore();
        await using var host = PhotoHost.Create(db.ConnectionString, FakePhotoReader.Answering(PhotoCanned.Recipe(PhotoCanned.Box())), store);
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);

        var (body, id) = await PhotoHost.ImportOkAsync(owner.Client, household);
        Assert.Equal($"/api/households/{household}/imports/{id}/picture", body.GetProperty("suggestedImage").GetProperty("url").GetString());
        Assert.True(body.GetProperty("useSuggestedImage").GetBoolean());

        using (var picture = await owner.Client.GetAsync(Url(household, $"/{id}/picture")))
        {
            Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
            Assert.Equal("image/webp", picture.Content.Headers.ContentType?.MediaType);
            Assert.True(picture.Headers.CacheControl is { NoStore: true, Private: true });
            using var decoded = NetVips.Image.NewFromBuffer(await picture.Content.ReadAsByteArrayAsync(Ct));
            Assert.Equal((720, 480), (decoded.Width, decoded.Height)); // 80% of 900 x 600
        }

        // Saving (the unmatched line removed) with the picture on: the recipe gets an image and the temporary blob is gone.
        await RemoveUnmatchedAsync(owner.Client, household, id);
        using var saved = await owner.Client.PostAsync(Url(household, $"/{id}/save"));
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var result = await saved.JsonAsync();
        Assert.True(result.GetProperty("pictureSaved").GetBoolean());
        var recipeId = result.GetProperty("recipeId").GetGuid();
        using (var recipe = await owner.Client.GetAsync($"/api/households/{household}/recipes/{recipeId}"))
        {
            Assert.NotNull((await recipe.JsonAsync()).GetProperty("image").GetProperty("cardUrl").GetString());
        }

        Assert.DoesNotContain(store.Blobs.Keys, k => k.Contains("/imports/", StringComparison.Ordinal));
        Assert.Equal(3, store.Blobs.Count); // thumb, card, full of the recipe image

        // Switched off with PUT: no image.
        var (_, off) = await PhotoHost.ImportOkAsync(owner.Client, household);
        using (var put = await owner.Client.PutAsync(Url(household, $"/{off}"), new { useSuggestedImage = false, lines = new[] { new { position = 2, remove = true } } }))
        {
            Assert.False((await put.JsonAsync()).GetProperty("useSuggestedImage").GetBoolean());
        }

        using var savedOff = await owner.Client.PostAsync(Url(household, $"/{off}/save"));
        var offBody = await savedOff.JsonAsync();
        Assert.Equal(JsonValueKind.Null, offBody.GetProperty("pictureSaved").ValueKind);
        using (var recipe = await owner.Client.GetAsync($"/api/households/{household}/recipes/{offBody.GetProperty("recipeId").GetGuid()}"))
        {
            Assert.Equal(JsonValueKind.Null, (await recipe.JsonAsync()).GetProperty("image").ValueKind);
        }

        Assert.DoesNotContain(store.Blobs.Keys, k => k.Contains("/imports/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhotoImport_picture_flow_a_failing_store_still_saves_the_recipe()
    {
        var store = new MemoryImageStore();
        await using var host = PhotoHost.Create(db.ConnectionString, FakePhotoReader.Answering(PhotoCanned.Recipe(PhotoCanned.Box())), store);
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);
        var (_, id) = await PhotoHost.ImportOkAsync(owner.Client, household);
        await RemoveUnmatchedAsync(owner.Client, household, id);

        store.FailPuts = true;
        using var saved = await owner.Client.PostAsync(Url(household, $"/{id}/save"));
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var body = await saved.JsonAsync();
        Assert.False(body.GetProperty("pictureSaved").GetBoolean());
        using var recipe = await owner.Client.GetAsync($"/api/households/{household}/recipes/{body.GetProperty("recipeId").GetGuid()}");
        Assert.Equal(HttpStatusCode.OK, recipe.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await recipe.JsonAsync()).GetProperty("image").ValueKind);
    }

    [Fact]
    public async Task PhotoImport_picture_flow_no_box_means_no_suggested_picture()
    {
        var store = new MemoryImageStore();
        await using var host = PhotoHost.Create(db.ConnectionString, FakePhotoReader.Answering(PhotoCanned.Recipe(null)), store);
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);
        var (body, id) = await PhotoHost.ImportOkAsync(owner.Client, household);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("suggestedImage").ValueKind);
        Assert.False(body.GetProperty("useSuggestedImage").GetBoolean());
        Assert.Empty(store.Blobs);
        using var picture = await owner.Client.GetAsync(Url(household, $"/{id}/picture"));
        await picture.AssertProblemAsync(HttpStatusCode.NotFound, "import-not-found");

        // A box that is invalid, or a crop that is too small, is not an error either.
        var tiny = new PhotoPicture(0, 0.0, 0.0, 0.2, 0.2, 0); // 180 x 120 px on a 900 x 600 photo
        var bad = new PhotoPicture(0, double.NaN, 0.1, 0.5, 0.5, 0);
        foreach (var picture2 in new[] { tiny, bad, PhotoCanned.Box() with { RotationDegrees = 45 }, PhotoCanned.Box() with { PhotoIndex = 9 } })
        {
            await using var other = PhotoHost.Create(db.ConnectionString, FakePhotoReader.Answering(PhotoCanned.Recipe(picture2)), store);
            var (user, home) = await PhotoHost.NewHouseholdAsync(other);
            var (response, _) = await PhotoHost.ImportOkAsync(user.Client, home);
            Assert.Equal(JsonValueKind.Null, response.GetProperty("suggestedImage").ValueKind);
        }

        Assert.Empty(store.Blobs);
    }

    [Fact]
    public async Task PhotoImport_crop_lives_under_the_household_prefix()
    {
        var store = new MemoryImageStore();
        await using var host = PhotoHost.Create(db.ConnectionString, FakePhotoReader.Answering(PhotoCanned.Recipe(PhotoCanned.Box())), store);
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);
        var (_, id) = await PhotoHost.ImportOkAsync(owner.Client, household);

        // The crop is under the household prefix, built from the row's own ids.
        var name = Assert.Single(store.Blobs.Keys);
        Assert.Equal($"{household:N}/imports/{id:N}/picture.webp", name);
    }

    private static async Task RemoveUnmatchedAsync(ApiClient client, Guid household, Guid id)
    {
        using var put = await client.PutAsync(Url(household, $"/{id}"), new { lines = new[] { new { position = 2, remove = true } } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }
}
