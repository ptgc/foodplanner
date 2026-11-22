using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Infrastructure.Import.Ai;
using Foodplanner.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foodplanner.Api.Tests.Import;

/// <summary>Hosts and calls for the photo import tests (T-118): always a fake or fake-HTTP reader, an in-memory image store.</summary>
internal static class PhotoHost
{
    public static AuthHost Create(
        string connectionString, IRecipePhotoReader reader, MemoryImageStore? store = null, Action<IServiceCollection>? more = null,
        params (string Key, string Value)[] settings)
    {
        var all = new Dictionary<string, string?>
        {
            ["AiImport:Enabled"] = "true",
            ["AiImport:ApiKey"] = "test-key-not-real",
            ["AiPhotoImport:Enabled"] = "true",
            ["AiPhotoImport:DailyPerHousehold"] = "50",
            ["AiPhotoImport:GlobalMonthly"] = "100000",
        };
        foreach (var (key, value) in settings)
        {
            all[key] = value;
        }

        var host = AuthHost.Create(
            connectionString,
            all,
            configureServices: services =>
            {
                services.RemoveAll<IRecipePhotoReader>();
                services.AddSingleton(reader);
                services.RemoveAll<IImageStore>();
                services.AddSingleton<IImageStore>(store ?? new MemoryImageStore());
                more?.Invoke(services);
            });
        host.DefaultLanguage = "nb";
        return host;
    }

    public static Task<HttpResponseMessage> PostPhotosAsync(ApiClient client, Guid household, int photos = 2, string? book = null) =>
        client.SendMultipartAsync(
            HttpMethod.Post, $"/api/households/{household}/imports/photo", PhotoFiles.Form(Enumerable.Range(0, photos).Select(_ => PhotoFiles.Jpeg()), book));

    public static Task<HttpResponseMessage> PostFormAsync(ApiClient client, Guid household, MultipartFormDataContent form) =>
        client.SendMultipartAsync(HttpMethod.Post, $"/api/households/{household}/imports/photo", form);

    public static async Task<(JsonElement Body, Guid Id)> ImportOkAsync(ApiClient client, Guid household, int photos = 2, string? book = null)
    {
        using var response = await PostPhotosAsync(client, household, photos, book);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.JsonAsync();
        return (body, body.GetProperty("id").GetGuid());
    }

    public static async Task<int> PhotoDayCallsAsync(string connectionString, Guid household) =>
        (int)(await HouseholdScenario.ScalarAsync(connectionString, "SELECT COALESCE(MAX(PhotoDayCalls), 0) FROM AiHouseholdUsages WHERE HouseholdId = @h", ("@h", household)))!;

    public static async Task<int> CountAsync(string connectionString, string table, Guid household) =>
        (int)(await HouseholdScenario.ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {table} WHERE HouseholdId = @h", ("@h", household)))!;

    public static async Task<(SignedInUser Owner, Guid Household)> NewHouseholdAsync(AuthHost host)
    {
        var owner = await host.SignInAsync();
        return (owner, await owner.Client.CreateHouseholdAsync());
    }
}
