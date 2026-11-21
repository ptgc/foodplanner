using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Foodplanner.Api.Tests.Oda;

internal sealed record FakeRequest(HttpMethod Method, Uri Url, string Body, string? Authorization);

/// <summary>
/// A fake Oda: the transport under the real client (no test ever reaches oda.com). Answers discovery, token, revoke and the three MCP
/// tools from recorded, scrubbed fixtures (spikes/oda/findings; tests/Api.Tests/Oda/Fixtures), and records every request. It keeps a
/// small in-memory cart so <c>manipulate_cart</c> and <c>get_cart</c> answer like the real shape. Knobs inject failures.
/// </summary>
internal sealed class FakeOda : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly List<FakeRequest> _requests = [];
    private readonly Dictionary<long, JsonNode> _catalog = [];
    private readonly Dictionary<long, int> _cart = [];
    private readonly Dictionary<string, long> _generated = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonNode _hits = Fixture("product-search.json");
    private string _accessToken = string.Empty;
    private int _revokeCalls;

    public static JsonNode Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Oda", "Fixtures", name)))!;

    public string AccessToken => Fixture("token.json")["access_token"]!.GetValue<string>();

    public string RefreshToken => Fixture("token.json")["refresh_token"]!.GetValue<string>();

    // ---- Knobs -------------------------------------------------------------------------------------------------------

    public bool IssueRefreshToken { get; set; } = true;

    /// <summary>Status the token endpoint answers with instead of the token (a refused code).</summary>
    public HttpStatusCode? TokenStatus { get; set; }

    /// <summary>Tool whose call answers HTTP 500 (<c>product_search</c>, <c>manipulate_cart</c>, <c>get_cart</c>).</summary>
    public string? FailTool { get; set; }

    /// <summary>Status every MCP call answers with (for example 429), after recording it.</summary>
    public HttpStatusCode? McpStatus { get; set; }

    /// <summary>Tool whose call is received and then never answered in time (the transport throws a timeout).</summary>
    public string? TimeoutTool { get; set; }

    public int RevokeFailures { get; set; }

    /// <summary>True when a revoke call arrived with an already cancelled token (it must not use the request's).</summary>
    public bool RevokeTokenWasCancelled { get; private set; }

    /// <summary>True when the revoke call carried a cancellable token, that is its own timeout.</summary>
    public bool RevokeHadOwnTimeout { get; private set; }

    public bool Sse { get; set; }

    /// <summary>Tools left out of <c>tools/list</c>.</summary>
    public HashSet<string> OmitTools { get; } = [];

    /// <summary>Input arguments removed from the tool schemas (<c>tool.argument</c>).</summary>
    public HashSet<string> OmitArguments { get; } = [];

    /// <summary>Response fields removed from the cart the tools return (<c>groups</c> from manipulate_cart).</summary>
    public HashSet<string> OmitCartFields { get; } = [];

    /// <summary>Product ids the cart refuses to take (the call succeeds but the line is not in the cart).</summary>
    public HashSet<long> RefuseProducts { get; } = [];

    /// <summary>Host the token endpoint is advertised on instead of oda.com.</summary>
    public string? TokenHost { get; set; }

    /// <summary>Called at the start of every tool call, before it is answered.</summary>
    public Action<string>? BeforeTool { get; set; }

    // ---- Records -----------------------------------------------------------------------------------------------------

    public IReadOnlyList<FakeRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    public IReadOnlyList<JsonNode> ToolCalls(string name) =>
        [.. Requests.Where(r => r.Url.AbsolutePath == "/mcp" && JsonOrNull(r.Body)?["method"]?.GetValue<string>() == "tools/call"
                                 && JsonOrNull(r.Body)!["params"]!["name"]!.GetValue<string>() == name)
            .Select(r => JsonNode.Parse(r.Body)!["params"]!["arguments"]!)];

    public IReadOnlyList<string> AllToolNames =>
        [.. Requests.Where(r => r.Url.AbsolutePath == "/mcp" && JsonOrNull(r.Body)?["method"]?.GetValue<string>() == "tools/call")
            .Select(r => JsonNode.Parse(r.Body)!["params"]!["name"]!.GetValue<string>())];

    public IReadOnlyList<FakeRequest> To(string path) => [.. Requests.Where(r => r.Url.AbsolutePath == path)];

    public IReadOnlyList<FakeRequest> RevokeRequests => To("/o/revoke_token/");

    public IReadOnlyList<FakeRequest> TokenRequests => To("/o/token/");

    public int RequestCount => Requests.Count;

    private static JsonNode? JsonOrNull(string body)
    {
        try
        {
            return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // ---- The transport -----------------------------------------------------------------------------------------------

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new FakeRequest(request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString());
        lock (_lock)
        {
            _requests.Add(recorded);
        }

        var path = request.RequestUri!.AbsolutePath;
        return path switch
        {
            "/.well-known/oauth-protected-resource/mcp" => Json(Fixture("prm.json")),
            "/.well-known/oauth-authorization-server/o" => Json(Metadata()),
            "/o/token/" => Token(),
            "/o/revoke_token/" => Revoke(body, cancellationToken),
            "/mcp" => Mcp(recorded, body, cancellationToken),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private JsonNode Metadata()
    {
        var meta = Fixture("as-metadata.json");
        if (TokenHost is not null)
        {
            meta["token_endpoint"] = $"https://{TokenHost}/o/token/";
        }

        return meta;
    }

    private HttpResponseMessage Token()
    {
        if (TokenStatus is { } status)
        {
            return new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"invalid_grant\",\"secret_detail\":\"upstream body\"}", Encoding.UTF8, "application/json") };
        }

        var token = Fixture("token.json");
        if (!IssueRefreshToken)
        {
            token.AsObject().Remove("refresh_token");
        }

        _accessToken = token["access_token"]!.GetValue<string>();
        return Json(token);
    }

    private HttpResponseMessage Revoke(string body, CancellationToken cancellationToken)
    {
        RevokeTokenWasCancelled |= cancellationToken.IsCancellationRequested;
        RevokeHadOwnTimeout = cancellationToken.CanBeCanceled;
        var call = Interlocked.Increment(ref _revokeCalls);
        if (call <= RevokeFailures)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }

        var form = body.Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        if (form.TryGetValue("token", out var token) && token == _accessToken)
        {
            _accessToken = "revoked";
        }

        return Json(new JsonObject());
    }

    private HttpResponseMessage Mcp(FakeRequest request, string body, CancellationToken cancellationToken)
    {
        if (McpStatus is { } forced)
        {
            return new HttpResponseMessage(forced);
        }

        if (request.Authorization != $"Bearer {_accessToken}")
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        var message = JsonNode.Parse(body)!;
        var method = message["method"]!.GetValue<string>();
        var id = message["id"];
        switch (method)
        {
            case "notifications/initialized":
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            case "initialize":
                var init = Rpc(id, new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "Oda MCP Server", ["version"] = "2.3.0" },
                });
                init.Headers.Add("Mcp-Session-Id", "fake-session");
                return init;
            case "tools/list":
                return Rpc(id, new JsonObject { ["tools"] = Tools() });
            case "tools/call":
                var name = message["params"]!["name"]!.GetValue<string>();
                var args = message["params"]!["arguments"]!;
                BeforeTool?.Invoke(name);
                cancellationToken.ThrowIfCancellationRequested(); // what the real transport does once the caller gave up
                if (TimeoutTool == name)
                {
                    throw new TaskCanceledException("The fake Oda never answered.", new TimeoutException());
                }

                if (FailTool == name)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("upstream-error-body-should-not-leak") };
                }

                return Rpc(id, name switch
                {
                    "product_search" => Wrap(new JsonObject { ["result"] = Search(args) }),
                    "manipulate_cart" => Wrap(Manipulate(args)),
                    "get_cart" => Wrap(CartJson(withUrl: true)),
                    _ => throw new InvalidOperationException($"The fake Oda has no tool '{name}': the app called something it must not."),
                });
            default:
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }
    }

    private JsonArray Tools()
    {
        var tools = new JsonArray();
        foreach (var tool in Fixture("tools.json").AsArray())
        {
            var name = tool!["name"]!.GetValue<string>();
            if (OmitTools.Contains(name))
            {
                continue;
            }

            var copy = tool.DeepClone();
            foreach (var argument in OmitArguments.Where(a => a.StartsWith(name + ".", StringComparison.Ordinal)))
            {
                copy["inputSchema"]!["properties"]!.AsObject().Remove(argument[(name.Length + 1)..]);
            }

            tools.Add(copy);
        }

        return tools;
    }

    private JsonArray Search(JsonNode args)
    {
        var rows = new JsonArray();
        foreach (var query in args["queries"]!.AsArray().Select(q => q!.GetValue<string>()))
        {
            var products = new JsonArray();
            if (_hits[query.ToLowerInvariant()] is JsonArray known)
            {
                foreach (var product in known)
                {
                    products.Add(product!.DeepClone());
                }
            }
            else if (!_hits.AsObject().ContainsKey(query.ToLowerInvariant()))
            {
                var id = _generated.TryGetValue(query, out var existing) ? existing : _generated[query] = 5000 + _generated.Count;
                products.Add(new JsonObject
                {
                    ["id"] = id,
                    ["name"] = $"Fake {query} pack",
                    ["description"] = query,
                    ["price"] = "10.00",
                    ["unitPrice"] = "10.00",
                    ["unitName"] = "stk",
                    ["availability"] = new JsonObject { ["isAvailable"] = true, ["description"] = "Available", ["code"] = "available" },
                });
            }

            foreach (var product in products)
            {
                _catalog[product!["id"]!.GetValue<long>()] = product.DeepClone();
            }

            rows.Add(new JsonObject { ["query"] = query, ["hasMore"] = false, ["products"] = products });
        }

        return rows;
    }

    private JsonNode Manipulate(JsonNode args)
    {
        foreach (var operation in args["operations"]!.AsArray())
        {
            var id = operation!["productId"]!.GetValue<long>();
            var quantity = operation["quantity"]!.GetValue<int>();
            if (quantity <= 0)
            {
                throw new InvalidOperationException("The app asked the cart to remove something: it only adds.");
            }

            if (!RefuseProducts.Contains(id))
            {
                _cart[id] = _cart.GetValueOrDefault(id) + quantity;
            }
        }

        return CartJson(withUrl: false);
    }

    private JsonNode CartJson(bool withUrl)
    {
        var cart = Fixture("cart.json");
        var items = cart["groups"]![0]!["items"]!.AsArray();
        foreach (var (id, quantity) in _cart)
        {
            items.Add(new JsonObject
            {
                ["product"] = _catalog.TryGetValue(id, out var product) ? product.DeepClone() : new JsonObject { ["id"] = id },
                ["quantity"] = quantity,
                ["totalGrossAmount"] = "10.00",
            });
        }

        cart["productQuantityCount"] = _cart.Values.Sum();
        if (!withUrl)
        {
            cart.AsObject().Remove("url");
        }

        foreach (var field in OmitCartFields)
        {
            cart.AsObject().Remove(field);
        }

        return cart;
    }

    private JsonObject Wrap(JsonNode structured) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = structured.ToJsonString() }),
        ["structuredContent"] = structured.DeepClone(),
        ["isError"] = false,
    };

    private HttpResponseMessage Rpc(JsonNode? id, JsonNode result)
    {
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result.DeepClone() };
        return Sse
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"event: message\ndata: {reply.ToJsonString()}\n\n", Encoding.UTF8, "text/event-stream") }
            : Json(reply);
    }

    private static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
}
