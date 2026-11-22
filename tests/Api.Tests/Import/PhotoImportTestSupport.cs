using Foodplanner.Api.Tests.Recipes;
using Foodplanner.Domain.Imports;
using Foodplanner.Infrastructure.Import.Ai;
using Foodplanner.Infrastructure.Storage;

namespace Foodplanner.Api.Tests.Import;

/// <summary>The default photo reader of every test host: any call is a bug in the test (T-118). verify.sh can never reach the real API.</summary>
internal sealed class NoRealPhotoReader : IRecipePhotoReader
{
    public Task<PhotoReadResult> ReadAsync(PhotoReadRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A test called the real photo reader. Register a FakePhotoReader.");
}

/// <summary>A fake Claude: records every request and answers with the handler.</summary>
internal sealed class FakePhotoReader(Func<PhotoReadRequest, CancellationToken, Task<PhotoReadResult>> handler) : IRecipePhotoReader
{
    private readonly List<PhotoReadRequest> _requests = [];

    public FakePhotoReader(Func<PhotoReadRequest, PhotoReadResult> answer)
        : this((request, _) => Task.FromResult(answer(request)))
    {
    }

    public IReadOnlyList<PhotoReadRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public int Calls => Requests.Count;

    public async Task<PhotoReadResult> ReadAsync(PhotoReadRequest request, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        return await handler(request, cancellationToken);
    }

    public static FakePhotoReader Answering(PhotoRecipe? recipe = null, int input = 1234, int output = 567) =>
        new(_ => new PhotoReadResult(PhotoReadOutcome.Read, recipe ?? PhotoCanned.Recipe(), input, output, "end_turn"));

    public static FakePhotoReader Outcome(PhotoReadOutcome outcome, int? input = 100, int? output = 50, string? stop = "end_turn") =>
        new(_ => new PhotoReadResult(outcome, null, input, output, stop));
}

internal static class PhotoCanned
{
    public const string PageText = "Zebrafisk-suppe PAGETEXT-MARKER trekk av varmen";

    /// <summary>A recipe the catalog can mostly match: cod, salt to taste and one ingredient that is not in the catalog.</summary>
    public static PhotoRecipe Recipe(PhotoPicture? picture = null, string? book = "Kokeboka mi") => new(
        true, true, "Bomba torsk", 2, 10, 20, "Kokkeknep: bruk dobbel folie.", book, PageText,
        ["Brett folien.", "Stek i 20 minutter."],
        [
            new PhotoIngredient("150 g torskeloin", "Torsk", "Measured", 150, null, "g", "Main", false, null, "torskeloin"),
            new PhotoIngredient("salt", "Salt", "ToTaste", null, null, null, "Seasoning", false, null, null),
            new PhotoIngredient("1 dl kryptonittsaus", "Kryptonittsaus", "Measured", 1, null, "dl", "Main", false, null, null),
        ],
        picture, []);

    public static PhotoPicture Box() => new(0, 0.1, 0.1, 0.8, 0.8, 0);
}

/// <summary>An in-memory image store that keeps what is put in it, and can be made to fail puts.</summary>
internal sealed class MemoryImageStore : IImageStore
{
    private readonly Dictionary<string, byte[]> _blobs = [];

    public bool FailPuts { get; set; }

    public IReadOnlyDictionary<string, byte[]> Blobs
    {
        get
        {
            lock (_blobs)
            {
                return new Dictionary<string, byte[]>(_blobs);
            }
        }
    }

    public Task PutAsync(string name, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        if (FailPuts)
        {
            throw new IOException("storage is down");
        }

        lock (_blobs)
        {
            _blobs[name] = content;
        }

        return Task.CompletedTask;
    }

    public Task<Stream?> OpenReadAsync(string name, CancellationToken cancellationToken)
    {
        lock (_blobs)
        {
            return Task.FromResult<Stream?>(_blobs.TryGetValue(name, out var bytes) ? new MemoryStream(bytes) : null);
        }
    }

    public Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        lock (_blobs)
        {
            foreach (var key in _blobs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _blobs.Remove(key);
            }
        }

        return Task.CompletedTask;
    }
}

internal static class PhotoFiles
{
    /// <summary>A JPEG carrying a recognisable marker in its EXIF (which the server must strip) and GPS.</summary>
    public const string Marker = "PHOTOMARKER-8f3a";

    public static byte[] Jpeg(int width = 900, int height = 600, int? orientation = null)
    {
        using var image = NetVips.Image.Black(width, height, bands: 3).NewFromImage(120, 160, 60);
        using var copy = image.Mutate(m =>
        {
            if (orientation is { } o)
            {
                m.Set(NetVips.GValue.GIntType, "orientation", o);
            }

            m.Set(NetVips.GValue.GStrType, "exif-ifd0-Make", Marker);
            m.Set(NetVips.GValue.GStrType, "exif-ifd3-GPSLatitudeRef", "N");
        });
        return copy.JpegsaveBuffer(q: 85);
    }

    public static MultipartFormDataContent Form(IEnumerable<byte[]> photos, string? sourceName = null, string partName = "photos")
    {
        var form = new MultipartFormDataContent();
        var index = 0;
        foreach (var photo in photos)
        {
            var content = new ByteArrayContent(photo);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            form.Add(content, partName, $"page-{index++}.jpg");
        }

        if (sourceName is not null)
        {
            form.Add(new StringContent(sourceName), "sourceName");
        }

        return form;
    }

    public static byte[] Png() => TestImages.Png(200, 200);
}
