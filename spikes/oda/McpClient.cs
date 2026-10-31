using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace OdaSpike;

/// <summary>Minimal MCP client over Streamable HTTP: JSON-RPC POSTs, replies as JSON or SSE.</summary>
internal sealed class McpClient : IDisposable
{
    private readonly HttpClient _http = new();
    private string? _session;
    private int _id;

    public JsonNode? ServerInfo { get; private set; }

    public static async Task<McpClient> ConnectAsync()
    {
        var c = new McpClient();
        var init = await c.RpcAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "foodplanner-oda-spike", ["version"] = "0.0.1" },
        });
        c.ServerInfo = init["serverInfo"];
        await c.NotifyAsync("notifications/initialized");
        return c;
    }

    public async Task<JsonNode> RpcAsync(string method, JsonNode? args)
    {
        var id = ++_id;
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = args };
        using var resp = await SendAsync(msg);
        var text = await resp.Content.ReadAsStringAsync();
        var type = resp.Content.Headers.ContentType?.MediaType;
        JsonNode? reply = null;
        if (type == "text/event-stream")
        {
            foreach (var line in text.Split('\n'))
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) { continue; }
                var node = JsonNode.Parse(line[5..].Trim());
                if (node?["id"]?.GetValue<int>() == id) { reply = node; }
            }
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            reply = JsonNode.Parse(text);
        }
        if (reply is null) { throw new InvalidOperationException($"No JSON-RPC reply (HTTP {(int)resp.StatusCode}, {type})."); }
        if (reply["error"] is { } err) { throw new InvalidOperationException($"MCP error from {method}: {err.ToJsonString()}"); }
        return reply["result"]!;
    }

    private async Task NotifyAsync(string method)
    {
        using var resp = await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });
    }

    private async Task<HttpResponseMessage> SendAsync(JsonObject msg)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, OAuth.McpUrl)
        {
            Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await OAuth.GetAccessTokenAsync());
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_session is not null) { req.Headers.Add("Mcp-Session-Id", _session); }
        var resp = await _http.SendAsync(req);
        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var s)) { _session = s.First(); }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");
        }
        return resp;
    }

    /// <summary>Calls a tool and returns its structured content (or the first text block parsed as JSON).</summary>
    public async Task<JsonNode> CallToolAsync(string name, JsonObject args)
    {
        var res = await RpcAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args });
        if (res["isError"]?.GetValue<bool>() == true) { throw new InvalidOperationException($"Tool {name} failed: {res["content"]?.ToJsonString()}"); }
        if (res["structuredContent"] is { } sc) { return sc.DeepClone(); }
        var text = res["content"]?[0]?["text"]?.GetValue<string>();
        if (text is null) { return res; }
        try { return JsonNode.Parse(text)!; } catch (System.Text.Json.JsonException) { return JsonValue.Create(text)!; }
    }

    public void Dispose() => _http.Dispose();
}
