using System.Net;
using System.Text;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Domain.Imports;
using Foodplanner.Infrastructure.Images;
using Foodplanner.Infrastructure.Import.Ai;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Tests.Import;

/// <summary>
/// The real SDK reader (T-118) against a fake HTTP handler: the request it builds, and how each kind of answer is read. No network:
/// the handler is the only transport. The same reader is also driven through the endpoint to check the logged outcomes.
/// </summary>
public sealed class AnthropicRecipePhotoReaderTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object ValidRecipe = new
    {
        isRecipe = true,
        readable = true,
        title = "Bomba torsk",
        servings = 2,
        prepMinutes = (int?)null,
        cookMinutes = 20,
        description = "Tips",
        sourceBook = (string?)null,
        rawText = "page",
        steps = new[] { "Stek." },
        ingredients = new[]
        {
            new
            {
                original = "150 g torsk", ingredient = "Torsk", amountKind = "Measured", quantity = (decimal?)150, quantityLow = (decimal?)null,
                unit = "g", purpose = "Main", isOptional = false, alternatives = Array.Empty<string>(), note = (string?)null,
            },
        },
        picture = (object?)null,
        warnings = Array.Empty<string>(),
    };

    internal static string MessageBody(string stopReason, string text, int input = 111, int output = 22) =>
        JsonSerializer.Serialize(new
        {
            id = "msg_test",
            type = "message",
            role = "assistant",
            model = "claude-opus-5-5",
            content = new[] { new { type = "text", text } },
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            usage = new { input_tokens = input, output_tokens = output },
        });

    private static AnthropicRecipePhotoReader Reader(FuncHandler handler, AiPhotoImportOptions? options = null) =>
        new(
            Options.Create(new AiImportOptions { Enabled = true, ApiKey = "test-key-not-real" }),
            Options.Create(options ?? new AiPhotoImportOptions { Enabled = true }),
            null,
            handler);

    private static PhotoReadRequest Request(int photos = 2, string? book = "Min bok") =>
        new([.. Enumerable.Range(0, photos).Select(i => new PreparedPhoto([0xFF, 0xD8, 0xFF, (byte)i], 10, 10))], book, ["g", "dl", "stk"]);

    private static FuncHandler Respond(HttpStatusCode status, string body, List<string>? captured = null) =>
        new((_, text) =>
        {
            captured?.Add(text);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        });

    [Fact]
    public async Task The_request_has_the_schema_effort_images_in_order_and_the_skill_as_system()
    {
        var bodies = new List<string>();
        var headers = new List<string>();
        var handler = new FuncHandler((request, text) =>
        {
            bodies.Add(text);
            headers.Add($"{request.Method} {request.RequestUri!.AbsolutePath} key-in={string.Join(",", request.Headers.Where(h => h.Value.Any(v => v.Contains("test-key-not-real", StringComparison.Ordinal))).Select(h => h.Key.ToLowerInvariant()))}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MessageBody("end_turn", JsonSerializer.Serialize(ValidRecipe)), Encoding.UTF8, "application/json"),
            });
        });
        var options = new AiPhotoImportOptions { Enabled = true, Effort = "high", MaxOutputTokens = 7777 };
        var result = await Reader(handler, options).ReadAsync(Request(), Ct);

        Assert.Equal(PhotoReadOutcome.Read, result.Outcome);
        Assert.Equal((111, 22), (result.InputTokens, result.OutputTokens));
        var call = Assert.Single(headers);
        Assert.Matches("^POST /v1/messages key-in=(x-api-key|authorization)$", call);

        using var json = JsonDocument.Parse(Assert.Single(bodies));
        var root = json.RootElement;
        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        Assert.Equal(7777, root.GetProperty("max_tokens").GetInt32());
        foreach (var forbidden in new[] { "temperature", "top_p", "top_k", "thinking", "tools", "tool_choice", "stream" })
        {
            Assert.False(root.TryGetProperty(forbidden, out _), $"{forbidden} must not be set");
        }

        var system = root.GetProperty("system");
        var systemText = system.ValueKind == JsonValueKind.String ? system.GetString() : string.Concat(system.EnumerateArray().Select(b => b.GetProperty("text").GetString()));
        Assert.Equal(RecipePhotoSkill.Text, systemText);

        var output = root.GetProperty("output_config");
        Assert.Equal("high", output.GetProperty("effort").GetString());
        Assert.Equal("json_schema", output.GetProperty("format").GetProperty("type").GetString());
        using var schema = JsonDocument.Parse(RecipePhotoSkill.SchemaJson);
        Assert.True(JsonElement.DeepEquals(schema.RootElement, output.GetProperty("format").GetProperty("schema")));

        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        var message = Assert.Single(messages);
        Assert.Equal("user", message.GetProperty("role").GetString());
        var blocks = message.GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(["text", "image", "text", "image", "text"], blocks.Select(b => b.GetProperty("type").GetString()));
        Assert.Equal("Photo 1", blocks[0].GetProperty("text").GetString());
        Assert.Equal("Photo 2", blocks[2].GetProperty("text").GetString());
        Assert.Equal(Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0]), blocks[1].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal(Convert.ToBase64String([0xFF, 0xD8, 0xFF, 1]), blocks[3].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("image/jpeg", blocks[1].GetProperty("source").GetProperty("media_type").GetString());
        var last = blocks[4].GetProperty("text").GetString()!;
        Assert.Contains("g, dl, stk", last, StringComparison.Ordinal);
        Assert.Contains("Min bok", last, StringComparison.Ordinal);
    }

    [Fact]
    public async Task There_are_no_automatic_retries()
    {
        var bodies = new List<string>();
        var result = await Reader(Respond(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"UPSTREAM-SECRET\"}}", bodies)).ReadAsync(Request(), Ct);
        Assert.Equal(PhotoReadOutcome.Failed, result.Outcome);
        Assert.Single(bodies); // one request, a retry would pay twice

        bodies.Clear();
        var overloaded = await Reader(Respond((HttpStatusCode)529, "{}", bodies)).ReadAsync(Request(), Ct);
        Assert.Equal(PhotoReadOutcome.Failed, overloaded.Outcome);
        Assert.Single(bodies);
    }

    [Theory]
    [InlineData("refusal", PhotoReadOutcome.Refused)]
    [InlineData("max_tokens", PhotoReadOutcome.MaxTokens)]
    [InlineData("tool_use", PhotoReadOutcome.Failed)]
    [InlineData("pause_turn", PhotoReadOutcome.Failed)]
    public async Task Only_end_turn_is_read_and_the_stop_reason_and_tokens_are_kept(string stop, PhotoReadOutcome expected)
    {
        // The content is a valid recipe: a refusal or a cut-off answer is still never parsed.
        var result = await Reader(Respond(HttpStatusCode.OK, MessageBody(stop, JsonSerializer.Serialize(ValidRecipe), 900, 33))).ReadAsync(Request(), Ct);
        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Recipe);
        Assert.Equal(stop, result.StopReason);
        Assert.Equal((900, 33), (result.InputTokens, result.OutputTokens));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"isRecipe\":true,\"readable\":true,\"title\":5}")]
    [InlineData("[1,2]")]
    public async Task A_schema_invalid_answer_is_a_failure_with_its_tokens(string text)
    {
        var result = await Reader(Respond(HttpStatusCode.OK, MessageBody("end_turn", text, 70, 8))).ReadAsync(Request(), Ct);
        Assert.Equal(PhotoReadOutcome.Failed, result.Outcome);
        Assert.Equal((70, 8), (result.InputTokens, result.OutputTokens));
    }

    [Fact]
    public async Task Not_a_recipe_and_unreadable_are_typed_outcomes()
    {
        var notRecipe = JsonSerializer.Serialize(new { isRecipe = false, readable = true });
        var unreadable = JsonSerializer.Serialize(new { isRecipe = true, readable = false });
        Assert.Equal(PhotoReadOutcome.NotARecipe, (await Reader(Respond(HttpStatusCode.OK, MessageBody("end_turn", notRecipe))).ReadAsync(Request(), Ct)).Outcome);
        Assert.Equal(PhotoReadOutcome.Unreadable, (await Reader(Respond(HttpStatusCode.OK, MessageBody("end_turn", unreadable))).ReadAsync(Request(), Ct)).Outcome);
    }

    [Fact]
    public async Task A_slow_answer_is_a_timeout_when_the_token_is_cancelled()
    {
        var slow = new FuncHandler(async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), Ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        var result = await Reader(slow).ReadAsync(Request(), cts.Token);
        Assert.Equal(PhotoReadOutcome.Timeout, result.Outcome);
    }

    // ---- Through the endpoint -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("refusal", HttpStatusCode.UnprocessableEntity, "photo-import-refused", "Refused", 800, 12)]
    [InlineData("max_tokens", HttpStatusCode.BadGateway, "photo-import-failed", "MaxTokens", 800, 12000)]
    public async Task PhotoImport_stop_reason_checked(string stop, HttpStatusCode status, string code, string outcome, int input, int output)
    {
        var reader = Reader(Respond(HttpStatusCode.OK, MessageBody(stop, JsonSerializer.Serialize(ValidRecipe), input, output)));
        await using var host = PhotoHost.Create(db.ConnectionString, reader);
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);

        using var response = await PhotoHost.PostPhotosAsync(owner.Client, household);
        await response.AssertProblemAsync(status, code);
        Assert.Equal(0, await PhotoHost.CountAsync(db.ConnectionString, "RecipeImports", household)); // the (cut or declined) content never became a draft
        await AssertLogAsync(household, outcome, stop, input, output);
    }

    [Fact]
    public async Task PhotoImport_stop_reason_checked_schema_invalid_body()
    {
        var reader = Reader(Respond(HttpStatusCode.OK, MessageBody("end_turn", "{\"isRecipe\":true}", 55, 5)));
        await using var host = PhotoHost.Create(db.ConnectionString, reader);
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);

        using var response = await PhotoHost.PostPhotosAsync(owner.Client, household);
        await response.AssertProblemAsync(HttpStatusCode.BadGateway, "photo-import-failed");
        await AssertLogAsync(household, "Failed", "end_turn", 55, 5);
    }

    [Fact]
    public async Task PhotoImport_a_real_reader_answer_becomes_a_draft_and_logs_tokens()
    {
        var reader = Reader(Respond(HttpStatusCode.OK, MessageBody("end_turn", JsonSerializer.Serialize(ValidRecipe), 4000, 900)));
        await using var host = PhotoHost.Create(db.ConnectionString, reader);
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);

        var (body, _) = await PhotoHost.ImportOkAsync(owner.Client, household);
        Assert.Equal("Bomba torsk", body.GetProperty("title").GetString());
        Assert.Equal("Matched", body.GetProperty("lines")[0].GetProperty("status").GetString());
        await AssertLogAsync(household, "Ok", "end_turn", 4000, 900);
    }

    private async Task AssertLogAsync(Guid household, string outcome, string stop, int input, int output)
    {
        async Task<object?> Get(string column) =>
            await HouseholdScenario.ScalarAsync(db.ConnectionString, $"SELECT {column} FROM AiCallLogs WHERE HouseholdId = @h", ("@h", household));
        Assert.Equal(outcome, await Get("Outcome"));
        Assert.Equal(stop, await Get("StopReason"));
        Assert.Equal(input, await Get("InputTokens"));
        Assert.Equal(output, await Get("OutputTokens"));
    }
}
