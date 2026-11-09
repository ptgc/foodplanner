using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Foodplanner.Infrastructure.Import.Ai;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Tests.Import;

/// <summary>The wire form of the AI call (T-077): what is sent, how the answer is read, how failures surface. No network: a fake handler.</summary>
public class AnthropicRecipeLineAiTests
{
    private static readonly AiImportOptions Settings = new() { Enabled = true, ApiKey = "test-key-not-real" };

    private static AnthropicRecipeLineAi Client(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> respond, AiImportOptions? settings = null) =>
        new(new HttpClient(new FuncHandler(respond)), Options.Create(settings ?? Settings));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string ToolUse(string linesJson) =>
        """{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"ok"},{"type":"tool_use","id":"t1","name":"submit_ingredient_lines","input":{"lines":""" + linesJson + """}}],"stop_reason":"tool_use"}""";

    private static readonly AiLineRequest Request = new(["2 dl dragefrukt", "???"], ["dl", "g"]);

    [Fact]
    public async Task The_request_is_exactly_the_lines_and_unit_codes_to_the_configured_model_with_a_forced_tool()
    {
        HttpRequestMessage? seen = null;
        string? sent = null;
        var client = Client((request, body) =>
        {
            seen = request;
            sent = body;
            return Task.FromResult(Json(ToolUse("[]")));
        });

        await client.ParseAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal("https://api.anthropic.com/v1/messages", seen.RequestUri!.ToString());
        Assert.Equal("test-key-not-real", Assert.Single(seen.Headers.GetValues("x-api-key")));
        Assert.Equal("2023-06-01", Assert.Single(seen.Headers.GetValues("anthropic-version")));

        using var document = JsonDocument.Parse(sent!);
        var root = document.RootElement;
        Assert.Equal("claude-haiku-5-5", root.GetProperty("model").GetString());
        Assert.Equal("submit_ingredient_lines", root.GetProperty("tool_choice").GetProperty("name").GetString());
        Assert.Equal("tool", root.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal("submit_ingredient_lines", root.GetProperty("tools")[0].GetProperty("name").GetString());
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        // The whole user message, exactly: only the line text and the unit codes.
        Assert.Equal(
            """{"unitCodes":["dl","g"],"lines":[{"index":0,"text":"2 dl dragefrukt"},{"index":1,"text":"???"}]}""",
            message.GetProperty("content").GetString());

        // Nothing identifying anywhere in the request: no GUID, no email address, no key.
        Assert.DoesNotMatch(new Regex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"), sent!);
        Assert.DoesNotMatch(new Regex(@"[\w.+-]+@[\w-]+\.[\w.]+"), sent!);
        Assert.DoesNotContain("test-key-not-real", sent!, StringComparison.Ordinal);
        Assert.DoesNotContain("householdId", sent!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_model_comes_from_the_options()
    {
        string? sent = null;
        var client = Client((_, body) => { sent = body; return Task.FromResult(Json(ToolUse("[]"))); }, new AiImportOptions { ApiKey = "k", Model = "some-other-model" });

        await client.ParseAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal("some-other-model", JsonDocument.Parse(sent!).RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task A_tool_answer_is_read_into_suggestions_and_a_malformed_line_becomes_null()
    {
        var client = Client((_, _) => Task.FromResult(Json(ToolUse("""
            [{"index":0,"ingredient":"smør","amountKind":"Measured","quantity":2.5,"quantityLow":null,"unit":"dl","purpose":"Frying","isOptional":true,"alternatives":["olje"],"note":"myknet"},
             {"index":1,"ingredient":"salt","amountKind":"ToTaste"},
             {"index":2,"ingredient":"x","amountKind":"Measured","quantity":"mye"},
             "not an object", {"ingredient":"no index"}]
            """))));

        var result = await client.ParseAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal(5, result.Count);
        var first = result[0]!;
        Assert.Equal((0, "smør", 2.5m, "dl", "Frying", true, "myknet"), (first.Index, first.Ingredient, first.Quantity, first.Unit, first.Purpose, first.IsOptional, first.Note));
        Assert.Equal(["olje"], first.Alternatives);
        Assert.Equal("ToTaste", result[1]!.AmountKind);
        Assert.Null(result[2]);
        Assert.Null(result[3]);
        Assert.Null(result[4]);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task An_http_error_is_an_unavailable_exception_without_the_body(HttpStatusCode status)
    {
        var client = Client((_, _) => Task.FromResult(Json("""{"error":{"message":"secret detail 2 dl dragefrukt"}}""", status)));

        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => client.ParseAsync(Request, TestContext.Current.CancellationToken));

        Assert.DoesNotContain("dragefrukt", ex.Message, StringComparison.Ordinal);
        Assert.Contains(((int)status).ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{}")]
    [InlineData("""{"content":[{"type":"text","text":"I cannot"}]}""")]
    [InlineData("""{"content":[{"type":"tool_use","name":"another_tool","input":{"lines":[]}}]}""")]
    [InlineData("""{"content":[{"type":"tool_use","name":"submit_ingredient_lines","input":{"lines":"nope"}}]}""")]
    public async Task An_answer_that_is_not_the_expected_json_is_an_unavailable_exception(string body)
    {
        var client = Client((_, _) => Task.FromResult(Json(body)));

        await Assert.ThrowsAsync<AiUnavailableException>(() => client.ParseAsync(Request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_network_failure_is_an_unavailable_exception_and_a_huge_answer_is_refused()
    {
        var down = Client((_, _) => throw new HttpRequestException("connection refused"));
        await Assert.ThrowsAsync<AiUnavailableException>(() => down.ParseAsync(Request, TestContext.Current.CancellationToken));

        var huge = Client((_, _) => Task.FromResult(Json(new string(' ', 300 * 1024) + "{}")));
        await Assert.ThrowsAsync<AiUnavailableException>(() => huge.ParseAsync(Request, TestContext.Current.CancellationToken));
    }
}
