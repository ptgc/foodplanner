using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OdaSpike;

// T-001 throwaway spike: Oda MCP feasibility. See README.md. Never pays, checks out, or touches addresses/delivery slots.
var cmd = args.Length > 0 ? args[0] : "help";
try
{
    switch (cmd)
    {
        case "login": await OAuth.LoginAsync(); break;
        case "tools": await ToolsAsync(); break;
        case "search": await SearchAsync(string.Join(' ', args.Skip(1))); break;
        case "search-catalog": await SearchCatalogAsync(); break;
        case "search-raw": await SearchRawCommandAsync(string.Join(' ', args.Skip(1))); break;
        case "recs": await RecsAsync(args.Length > 1 ? args[1] : ""); break;
        case "cart": await CartAsync(); break;
        case "add": await AddAsync(long.Parse(args[1]), int.Parse(args[2])); break;
        case "clear-test-items": case "remove": await ClearTestItemsAsync(); break;
        default:
            Console.WriteLine("Commands: login | tools | search <text> | search-raw <text> | recs <likely_to_buy|unique_for_you> | search-catalog | cart | add <productId> <qty> | clear-test-items");
            break;
    }
}
catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TimeoutException or FormatException or IndexOutOfRangeException)
{
    Console.Error.WriteLine("FAILED: " + e.Message);
    return 1;
}
return 0;

static string FindingsDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OdaSpike.csproj"))) { dir = dir.Parent; }
    var path = Path.Combine(dir?.FullName ?? Directory.GetCurrentDirectory(), "findings");
    Directory.CreateDirectory(path);
    return path;
}

static async Task ToolsAsync()
{
    using var mcp = await McpClient.ConnectAsync();
    Console.WriteLine($"Server: {mcp.ServerInfo?.ToJsonString()}");
    var tools = new JsonArray();
    string? cursor = null;
    do
    {
        var res = await mcp.RpcAsync("tools/list", cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor });
        foreach (var t in res["tools"]!.AsArray()) { tools.Add(t!.DeepClone()); }
        cursor = res["nextCursor"]?.GetValue<string>();
    } while (cursor is not null);
    var file = Path.Combine(FindingsDir(), "tools.json");
    File.WriteAllText(file, tools.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    foreach (var t in tools) { Console.WriteLine($"- {t!["name"]}: {Truncate(t["description"]?.GetValue<string>(), 110)}"); }
    Console.WriteLine($"{tools.Count} tools saved to {file}");
}

static async Task<JsonArray> SearchRawAsync(McpClient mcp, string[] queries, int size)
{
    var res = await mcp.CallToolAsync("product_search", new JsonObject
    {
        ["queries"] = new JsonArray(queries.Select(q => (JsonNode)JsonValue.Create(q)!).ToArray()),
        ["size"] = size,
        ["intent"] = "foodplanner feasibility spike: checks search quality for household ingredient names",
    });
    return (res["result"] ?? res).AsArray();
}

static async Task SearchAsync(string text)
{
    using var mcp = await McpClient.ConnectAsync();
    var results = await SearchRawAsync(mcp, [text], 5);
    foreach (var r in results)
    {
        foreach (var p in r!["products"]!.AsArray())
        {
            Console.WriteLine($"{p!["name"]} | {p["price"]} | {p["unitPrice"]} {p["unitName"]} | brand {p["brand"]} | id {p["id"]} | image: {ImageOf(p)} | {p["url"]}");
        }
    }
    if (results.Count > 0 && results[0]!["products"]!.AsArray().FirstOrDefault() is { } first)
    {
        Console.WriteLine("Fields on a product: " + string.Join(", ", first.AsObject().Select(kv => kv.Key)));
    }
}

static System.Text.RegularExpressions.Regex OfferLike() => new(
    "offer|campaign|discount|promo|sale|deal|original|before|compare|badge|label",
    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

static JsonSerializerOptions Indented() => new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

// Read-only: prints full raw product JSON so offer/campaign fields (T-107) are visible.
static async Task SearchRawCommandAsync(string text)
{
    if (string.IsNullOrWhiteSpace(text)) { throw new InvalidOperationException("search-raw needs a search text."); }
    using var mcp = await McpClient.ConnectAsync();
    var results = await SearchRawAsync(mcp, [text], 3);
    foreach (var r in results)
    {
        foreach (var p in r!["products"]?.AsArray() ?? [])
        {
            Console.WriteLine(p!.ToJsonString(Indented()));
            PrintOfferFields(p);
        }
    }
}

static void PrintOfferFields(JsonNode p)
{
    var hits = p is JsonObject o ? o.Select(kv => kv.Key).Where(k => OfferLike().IsMatch(k)).ToList() : [];
    Console.WriteLine(hits.Count == 0 ? "no offer-like fields" : "offer-like fields: " + string.Join(", ", hits));
}

static IEnumerable<JsonObject> FindProducts(JsonNode? node)
{
    switch (node)
    {
        case JsonObject o when o.ContainsKey("name") && (o.ContainsKey("id") || o.ContainsKey("price")):
            yield return o;
            break;
        case JsonObject o:
            foreach (var kv in o) { foreach (var x in FindProducts(kv.Value)) { yield return x; } }
            break;
        case JsonArray a:
            foreach (var i in a) { foreach (var x in FindProducts(i)) { yield return x; } }
            break;
    }
}

// Read-only recommendation tools; strict allowlist.
static async Task RecsAsync(string tool)
{
    if (tool is not ("likely_to_buy" or "unique_for_you")) { throw new InvalidOperationException("recs accepts only likely_to_buy or unique_for_you."); }
    using var mcp = await McpClient.ConnectAsync();
    // Schema (findings/tools.public-manifest.json) has only "intent"; no size/limit property.
    var res = await mcp.CallToolAsync(tool, new JsonObject { ["intent"] = "foodplanner feasibility spike: inspects recommendation fields" });
    Console.WriteLine(res.ToJsonString(Indented()));
    foreach (var p in FindProducts(res))
    {
        Console.WriteLine($"product {p["id"]} {p["name"]}:");
        PrintOfferFields(p);
    }
}

static string ImageOf(JsonNode p) =>
    (p["image"] ?? p["imageUrl"] ?? p["images"] ?? p["thumbnail"])?.ToJsonString() ?? "(none in response)";

static async Task SearchCatalogAsync()
{
    // nb names from src/Domain/Catalog/IngredientSeed.cs
    string[] names =
    [
        "melk", "smør", "egg", "kyllingfilet", "laks", "løk", "hvitløk", "gulrot", "potet", "tomat",
        "pasta", "ris", "rømme", "Nøytral olje", "Margarin", "hvetemel", "sukker", "fløte", "brokkoli", "karbonadedeig",
    ];
    using var mcp = await McpClient.ConnectAsync();
    var sb = new StringBuilder();
    sb.AppendLine("# Oda product search quality (nb catalog names)").AppendLine();
    sb.AppendLine($"Generated {DateTime.UtcNow:yyyy-MM-dd} by `search-catalog`. Query is the Foodplanner catalog name; top 3 hits (name, price, id). Judge relevance by eye.").AppendLine();
    sb.AppendLine("| Query | Top 3 hits |").AppendLine("|---|---|");
    foreach (var batch in names.Chunk(10))
    {
        foreach (var r in await SearchRawAsync(mcp, batch, 3))
        {
            var hits = r!["products"]!.AsArray().Select(p => $"{p!["name"]} ({p["price"]}, id {p["id"]})");
            sb.AppendLine($"| {r["query"]} | {string.Join("<br>", hits)} |");
        }
    }
    var file = Path.Combine(FindingsDir(), "search-quality.md");
    File.WriteAllText(file, sb.ToString());
    Console.WriteLine(sb.ToString());
    Console.WriteLine($"Saved to {file}");
}

static async Task CartAsync()
{
    using var mcp = await McpClient.ConnectAsync();
    var cart = await mcp.CallToolAsync("get_cart", new JsonObject { ["intent"] = "foodplanner feasibility spike: inspects cart" });
    PrintCart(cart);
}

static void PrintCart(JsonNode cart)
{
    Console.WriteLine($"Cart: {cart["productQuantityCount"]} items, total {cart["totalGrossAmount"]}");
    foreach (var g in cart["groups"]?.AsArray() ?? [])
    {
        foreach (var line in g!["items"]?.AsArray() ?? [])
        {
            Console.WriteLine($"  {line!["quantity"]} x {line["product"]?["name"]} (id {line["product"]?["id"]}) {line["totalGrossAmount"]}");
        }
    }
    if (cart["validationMessages"] is { } vm) { Console.WriteLine("Validation: " + vm.ToJsonString()); }
}

static JsonObject Tracked() =>
    Secrets.Get("Oda:SpikeAdded") is { } s ? (JsonNode.Parse(s) as JsonObject ?? []) : [];

static async Task AddAsync(long productId, int qty)
{
    if (qty <= 0) { throw new InvalidOperationException("qty must be positive."); }
    using var mcp = await McpClient.ConnectAsync();
    // Record first, so a crash after the call still lets clear-test-items undo it (removal overshoot is safe on the server side).
    var tracked = Tracked();
    tracked[productId.ToString()] = (tracked[productId.ToString()]?.GetValue<int>() ?? 0) + qty;
    Secrets.Set(("Oda:SpikeAdded", tracked.ToJsonString()));
    var cart = await mcp.CallToolAsync("manipulate_cart", new JsonObject
    {
        ["operations"] = new JsonArray(new JsonObject { ["productId"] = productId, ["quantity"] = qty }),
        ["intent"] = "foodplanner feasibility spike: adds one test item",
    });
    PrintCart(cart);
}

static async Task ClearTestItemsAsync()
{
    var tracked = Tracked();
    if (tracked.Count == 0) { Console.WriteLine("Nothing recorded as added by the spike."); return; }
    using var mcp = await McpClient.ConnectAsync();
    // Only the exact delta the spike added is removed (manipulate_cart quantities are relative), never clear_cart.
    var ops = new JsonArray(tracked.Select(kv => (JsonNode)new JsonObject { ["productId"] = long.Parse(kv.Key), ["quantity"] = -kv.Value!.GetValue<int>() }).ToArray());
    var cart = await mcp.CallToolAsync("manipulate_cart", new JsonObject { ["operations"] = ops, ["intent"] = "foodplanner feasibility spike: removes its own test items" });
    Secrets.Set(("Oda:SpikeAdded", null));
    PrintCart(cart);
}

static string Truncate(string? s, int n) => s is null ? "" : s.Length <= n ? s.ReplaceLineEndings(" ") : s[..n].ReplaceLineEndings(" ") + "...";
