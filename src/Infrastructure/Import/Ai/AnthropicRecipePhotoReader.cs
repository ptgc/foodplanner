using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Foodplanner.Domain.Imports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Foodplanner.Infrastructure.Import.Ai;

/// <summary>
/// <see cref="IRecipePhotoReader"/> over the official Anthropic C# SDK (T-118, Q-037; T-077's raw HTTP call stays as it is). The
/// client has the fixed <see cref="AiImportOptions.BaseUrl"/>, the shared key in the header only, no automatic retries (a retry
/// would pay twice) and no redirects. The request is built by <see cref="BuildRequest"/>; the answer is read in the order of the
/// spec: error or timeout, then <c>stop_reason</c> (only <c>end_turn</c> is read), then the JSON, then the flags. Photos, page text
/// and upstream bodies are never logged or put in exceptions.
/// </summary>
public sealed class AnthropicRecipePhotoReader : IRecipePhotoReader, IDisposable
{
    private readonly IOptions<AiImportOptions> _shared;
    private readonly IOptions<AiPhotoImportOptions> _options;
    private readonly ILogger<AnthropicRecipePhotoReader>? _logger;
    private readonly HttpMessageHandler? _handler;
    private readonly object _gate = new();
    private AnthropicClient? _client;

    /// <param name="handler">Tests pass a fake handler; production leaves it null and gets a pooled handler that never follows redirects.</param>
    public AnthropicRecipePhotoReader(
        IOptions<AiImportOptions> shared, IOptions<AiPhotoImportOptions> options, ILogger<AnthropicRecipePhotoReader>? logger = null,
        HttpMessageHandler? handler = null)
    {
        _shared = shared;
        _options = options;
        _logger = logger;
        _handler = handler;
    }

    public static MessageCreateParams BuildRequest(PhotoReadRequest request, AiPhotoImportOptions options)
    {
        var content = new List<ContentBlockParam>();
        for (var i = 0; i < request.Photos.Count; i++)
        {
            content.Add(new TextBlockParam { Text = $"Photo {i + 1}" });
            content.Add(new ImageBlockParam
            {
                Source = new Base64ImageSource { Data = Convert.ToBase64String(request.Photos[i].Jpeg), MediaType = MediaType.ImageJpeg },
            });
        }

        var text = $"Unit codes: {string.Join(", ", request.UnitCodes)}.";
        if (!string.IsNullOrWhiteSpace(request.SourceName))
        {
            text += $" Book name typed by the user: {request.SourceName.Trim()}";
        }

        content.Add(new TextBlockParam { Text = text });

        var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(RecipePhotoSkill.SchemaJson)!;
        return new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = options.MaxOutputTokens,
            System = RecipePhotoSkill.Text,
            Messages = [new UserMessageParam { Content = content }],
            OutputConfig = new OutputConfig
            {
                Effort = Enum.TryParse<Effort>(options.Effort, true, out var effort) ? effort : Effort.Medium,
                Format = new JsonOutputFormat { Schema = schema },
            },
        };
    }

    public async Task<PhotoReadResult> ReadAsync(PhotoReadRequest request, CancellationToken cancellationToken)
    {
        var settings = _options.Value;
        Message message;
        AnthropicClient client;
        MessageCreateParams parameters;
        try
        {
            parameters = BuildRequest(request, settings);
            client = Client();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing was sent: the caller gives the reservation back.
            throw new PhotoReaderNotReadyException(ex);
        }

        try
        {
            message = await client.Messages.Create(parameters, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new PhotoReadResult(PhotoReadOutcome.Timeout, null, null, null, null);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("The photo import call failed ({Error}).", ex.GetType().Name);
            return new PhotoReadResult(PhotoReadOutcome.Failed, null, null, null, null);
        }

        try
        {
            int? input = message.Usage is { } usage ? (int)usage.InputTokens : null;
            int? output = message.Usage is { } usage2 ? (int)usage2.OutputTokens : null;
            var stop = message.StopReason?.ToString().Trim().Trim((char)34);
            var reason = stop?.ToLowerInvariant();

            if (reason == "refusal")
            {
                return new PhotoReadResult(PhotoReadOutcome.Refused, null, input, output, "refusal");
            }

            if (reason == "max_tokens")
            {
                return new PhotoReadResult(PhotoReadOutcome.MaxTokens, null, input, output, "max_tokens");
            }

            if (reason != "end_turn")
            {
                return new PhotoReadResult(PhotoReadOutcome.Failed, null, input, output, stop);
            }

            string? json = null;
            foreach (var block in message.Content)
            {
                if (block.TryPickText(out var textBlock))
                {
                    json = textBlock.Text;
                    break;
                }
            }

            var recipe = PhotoRecipeParser.Parse(json);
            if (recipe is null)
            {
                return new PhotoReadResult(PhotoReadOutcome.Failed, null, input, output, stop);
            }

            if (!recipe.IsRecipe)
            {
                return new PhotoReadResult(PhotoReadOutcome.NotARecipe, null, input, output, stop);
            }

            return recipe.Readable
                ? new PhotoReadResult(PhotoReadOutcome.Read, recipe, input, output, stop)
                : new PhotoReadResult(PhotoReadOutcome.Unreadable, null, input, output, stop);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("The photo import answer could not be read ({Error}).", ex.GetType().Name);
            return new PhotoReadResult(PhotoReadOutcome.Failed, null, null, null, null);
        }
    }

    private AnthropicClient Client()
    {
        lock (_gate)
        {
            if (_client is not null)
            {
                return _client;
            }

            var shared = _shared.Value;
            var settings = _options.Value;
            var http = new HttpClient(
                _handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false },
                disposeHandler: _handler is null)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _client = new AnthropicClient(new ClientOptions
            {
                HttpClient = http,
                ApiKey = shared.ApiKey,
                BaseUrl = shared.BaseUrl,
                MaxRetries = 0,
                Timeout = TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)),
            });
            return _client;
        }
    }

    public void Dispose() => _client?.Dispose();
}
