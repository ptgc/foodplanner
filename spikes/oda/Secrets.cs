using System.Text.Json;
using System.Text.Json.Nodes;

namespace OdaSpike;

/// <summary>
/// Reads/writes the same secrets.json that `dotnet user-secrets` uses (UserSecretsId in the csproj),
/// so `dotnet user-secrets clear --project spikes/oda` removes everything. Values are never printed.
/// </summary>
internal static class Secrets
{
    private const string Id = "foodplanner-oda-spike-t001";

    private static string FilePath
    {
        get
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            var root = !string.IsNullOrEmpty(appData)
                ? Path.Combine(appData, "Microsoft", "UserSecrets")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".microsoft", "usersecrets");
            return Path.Combine(root, Id, "secrets.json");
        }
    }

    private static JsonObject Load() =>
        File.Exists(FilePath) ? (JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? []) : [];

    public static string? Get(string key) => Load()[key]?.GetValue<string>();

    public static void Set(params (string Key, string? Value)[] entries)
    {
        var obj = Load();
        foreach (var (key, value) in entries)
        {
            if (value is null) { obj.Remove(key); } else { obj[key] = value; }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
