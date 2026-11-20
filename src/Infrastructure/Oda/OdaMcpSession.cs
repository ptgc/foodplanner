using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Foodplanner.Domain.ShoppingLists.Cart;
using Microsoft.Extensions.Options;

namespace Foodplanner.Infrastructure.Oda;

/// <summary>Opens <see cref="OdaMcpSession"/>s. Holds no state of its own.</summary>
public sealed class OdaMcpSessionFactory(OdaHttp http, IOptions<OdaOptions> options)
{
    public async Task<OdaMcpSession> OpenAsync(OdaSecret accessToken, CancellationToken cancellationToken)
    {
        var session = new OdaMcpSession(http, options.Value, accessToken);
        await session.InitializeAsync(cancellationToken);
        return session;
    }
}

/// <summary>
/// A minimal MCP client (Streamable HTTP, JSON-RPC; replies as JSON or SSE) for one action, with the access token in memory
/// (T-053). Oda's MCP server changes over time (2.0.0 to 2.3.0 within weeks), so tool schemas are treated as unstable: a missing
/// tool, argument or response field fails closed with <see cref="ProviderUnavailableException"/>. It calls only the tools
/// <c>product_search</c>, <c>manipulate_cart</c> (positive quantities only) and <c>get_cart</c>, and never retries a call.
/// </summary>
public sealed class OdaMcpSession : ICartProviderSession
{
    private const string ProtocolVersion = "2025-06-18";

    private readonly OdaHttp _http;
    private readonly OdaOptions _options;
    private readonly OdaSecret _token;
    private readonly Uri _mcp;
    private string? _sessionId;
    private int _nextId;
    private bool _searchHasSize;
    private bool _searchHasIntent;
    private bool _cartHasIntent;
    private bool _getCartHasIntent;

    internal OdaMcpSession(OdaHttp http, OdaOptions options, OdaSecret token)
    {
        _http = http;
        _options = options;
        _token = token;
        _mcp = Uri.TryCreate(options.McpUrl, UriKind.Absolute, out var uri) && options.IsAllowed(uri)
            ? uri
            : throw new ProviderUnavailableException("The Oda server address is not allowed.");
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RpcAsync(
            "initialize",
            new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "foodplanner", ["version"] = "1.0" },
            },
            cancellationToken);
        await NotifyAsync("notifications/initialized", cancellationToken);
        await CheckToolsAsync(cancellationToken);
    }

    /// <summary>Reads the tool list once and requires the three tools and the arguments we send; schemas are not trusted to stay as recorded.</summary>
    private async Task CheckToolsAsync(CancellationToken cancellationToken)
    {
        var tools = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var result = await RpcAsync("tools/list", cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor }, cancellationToken);
            foreach (var tool in result["tools"] as JsonArray ?? throw Unexpected())
            {
                if (tool is JsonObject obj && obj["name"] is JsonValue name && name.TryGetValue<string>(out var text))
                {
                    tools[text] = obj;
                }
            }

            cursor = result["nextCursor"] is JsonValue next && next.TryGetValue<string>(out var c) && !string.IsNullOrEmpty(c) ? c : null;
            if (cursor is null)
            {
                break;
            }
        }

        var search = Properties(tools, "product_search", "queries");
        var cart = Properties(tools, "manipulate_cart", "operations");
        var get = Properties(tools, "get_cart");
        _searchHasSize = search.ContainsKey("size");
        _searchHasIntent = search.ContainsKey("intent");
        _cartHasIntent = cart.ContainsKey("intent");
        _getCartHasIntent = get.ContainsKey("intent");
    }

    private static JsonObject Properties(Dictionary<string, JsonObject> tools, string tool, params string[] required)
    {
        if (!tools.TryGetValue(tool, out var definition))
        {
            throw new ProviderUnavailableException("A tool this app needs is missing at Oda.");
        }

        var properties = definition["inputSchema"]?["properties"] as JsonObject ?? [];
        if (required.Any(r => !properties.ContainsKey(r)))
        {
            throw new ProviderUnavailableException("A tool at Oda no longer takes the arguments this app sends.");
        }

        return properties;
    }

    // ---- ICartProviderSession ----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<IReadOnlyList<ProductCandidate>>> SearchAsync(IReadOnlyList<string> queries, CancellationToken cancellationToken)
    {
        var arguments = new JsonObject { ["queries"] = new JsonArray([.. queries.Select(q => (JsonNode)JsonValue.Create(q))]) };
        if (_searchHasSize)
        {
            arguments["size"] = Math.Clamp(_options.SearchPageSize, 1, 20);
        }

        if (_searchHasIntent)
        {
            arguments["intent"] = "adds this week's shopping list items to the cart";
        }

        var result = await CallToolAsync("product_search", arguments, cancellationToken);
        if ((result["result"] ?? result) is not JsonArray rows)
        {
            throw Unexpected();
        }

        var all = new List<IReadOnlyList<ProductCandidate>>();
        foreach (var row in rows)
        {
            if (row?["products"] is not JsonArray products)
            {
                throw Unexpected();
            }

            all.Add([.. products.Select(ToCandidate)]);
        }

        return all;
    }

    public async Task<IReadOnlySet<string>> AddOnePackEachAsync(IReadOnlyList<string> productIds, CancellationToken cancellationToken)
    {
        var operations = new JsonArray();
        foreach (var id in productIds)
        {
            if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var productId))
            {
                throw Unexpected();
            }

            // Only ever +1: this client adds, it never removes or clears.
            operations.Add(new JsonObject { ["productId"] = productId, ["quantity"] = 1 });
        }

        var arguments = new JsonObject { ["operations"] = operations };
        if (_cartHasIntent)
        {
            arguments["intent"] = "adds this week's shopping list items to the cart";
        }

        var cart = await CallToolAsync("manipulate_cart", arguments, cancellationToken);
        return CartProductIds(cart);
    }

    public async Task<CartSnapshot> ReadCartAsync(CancellationToken cancellationToken)
    {
        var arguments = new JsonObject();
        if (_getCartHasIntent)
        {
            arguments["intent"] = "shows the cart link after adding the shopping list";
        }

        var cart = await CallToolAsync("get_cart", arguments, cancellationToken);
        string? url = null;
        if (cart["url"] is JsonValue value && value.TryGetValue<string>(out var text)
            && Uri.TryCreate(text, UriKind.Absolute, out var uri) && _options.IsAllowed(uri))
        {
            url = uri.AbsoluteUri;
        }

        var messages = new List<string>();
        if (cart["validationMessages"] is JsonArray array)
        {
            foreach (var item in array.Take(20))
            {
                if (item is JsonValue m && m.TryGetValue<string>(out var message) && !string.IsNullOrWhiteSpace(message))
                {
                    messages.Add(message.Trim().Length > 300 ? message.Trim()[..300] : message.Trim());
                }
            }
        }

        return new CartSnapshot(url, messages);
    }

    // ---- Shapes ------------------------------------------------------------------------------------------------------

    private static ProductCandidate ToCandidate(JsonNode? product)
    {
        if (product is not JsonObject obj
            || obj["id"] is not JsonValue id
            || obj["name"] is not JsonValue name || !name.TryGetValue<string>(out var text))
        {
            throw Unexpected();
        }

        string productId;
        if (id.TryGetValue<long>(out var number))
        {
            productId = number.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            throw Unexpected();
        }

        var available = obj["availability"]?["isAvailable"] is JsonValue flag && flag.TryGetValue<bool>(out var isAvailable) && isAvailable;
        return new ProductCandidate(productId, text, available);
    }

    private static HashSet<string> CartProductIds(JsonNode cart)
    {
        if (cart["groups"] is not JsonArray groups)
        {
            throw Unexpected();
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (group?["items"] is not JsonArray items)
            {
                throw Unexpected();
            }

            foreach (var line in items)
            {
                if (line?["product"]?["id"] is JsonValue id && id.TryGetValue<long>(out var number))
                {
                    ids.Add(number.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        return ids;
    }

    private static ProviderUnavailableException Unexpected() => new("Oda answered in a shape this app does not know.");

    // ---- JSON-RPC ----------------------------------------------------------------------------------------------------

    private async Task<JsonNode> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
    {
        var result = await RpcAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments }, cancellationToken);
        if (result["isError"] is JsonValue flag && flag.TryGetValue<bool>(out var isError) && isError)
        {
            throw new ProviderUnavailableException("Oda's tool reported an error.");
        }

        if (result["structuredContent"] is JsonObject structured)
        {
            return structured;
        }

        if (result["content"] is JsonArray { Count: > 0 } content && content[0]?["text"] is JsonValue text && text.TryGetValue<string>(out var json))
        {
            try
            {
                return JsonNode.Parse(json) ?? throw Unexpected();
            }
            catch (JsonException)
            {
                throw Unexpected();
            }
        }

        throw Unexpected();
    }

    private async Task NotifyAsync(string method, CancellationToken cancellationToken)
    {
        var response = await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }, cancellationToken);
        Check(response);
    }

    private async Task<JsonNode> RpcAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = ++_nextId;
        var response = await SendAsync(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, cancellationToken);
        Check(response);

        JsonNode? reply = null;
        try
        {
            if (string.Equals(response.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in response.Body.Split('\n'))
                {
                    if (line.StartsWith("data:", StringComparison.Ordinal) && JsonNode.Parse(line[5..].Trim()) is JsonObject node
                        && node["id"] is JsonValue nodeId && nodeId.TryGetValue<int>(out var replyId) && replyId == id)
                    {
                        reply = node;
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(response.Body))
            {
                reply = JsonNode.Parse(response.Body);
            }
        }
        catch (JsonException)
        {
            throw Unexpected();
        }

        if (reply is not JsonObject obj || obj["error"] is not null || obj["result"] is not JsonNode result)
        {
            throw Unexpected();
        }

        return result;
    }

    private async Task<OdaResponse> SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _mcp)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token.Reveal());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_sessionId is not null)
        {
            request.Headers.Add("Mcp-Session-Id", _sessionId);
        }

        var response = await _http.SendAsync(request, cancellationToken);
        _sessionId ??= response.SessionId;
        return response;
    }

    private static void Check(OdaResponse response)
    {
        if (response.Status == HttpStatusCode.TooManyRequests)
        {
            throw new ProviderUnavailableException("Oda's usage limit was reached.");
        }

        if (response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ProviderUnavailableException("Oda no longer accepts the sign-in.");
        }

        if (!response.IsSuccess)
        {
            throw new ProviderUnavailableException("Oda answered with an error.");
        }
    }
}
