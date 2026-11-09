using Foodplanner.Domain.Imports;
using Foodplanner.Infrastructure.Import.Ai;

namespace Foodplanner.Api.Tests.Import;

/// <summary>
/// The default AI client of every test host: any call is a bug in the test (T-077). A test that exercises the fallback replaces it
/// with <see cref="FakeRecipeLineAi"/>, so verify.sh can never reach the real API.
/// </summary>
internal sealed class NoRealAiClient : IRecipeLineAi
{
    public Task<IReadOnlyList<AiLineSuggestion?>> ParseAsync(AiLineRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A test called the real AI client. Register a FakeRecipeLineAi.");
}

/// <summary>A fake LLM: records every request it gets and answers with the handler.</summary>
internal sealed class FakeRecipeLineAi(Func<AiLineRequest, CancellationToken, Task<IReadOnlyList<AiLineSuggestion?>>> handler) : IRecipeLineAi
{
    private readonly List<AiLineRequest> _requests = [];

    public FakeRecipeLineAi(Func<AiLineRequest, IReadOnlyList<AiLineSuggestion?>> answer)
        : this((request, _) => Task.FromResult(answer(request)))
    {
    }

    public IReadOnlyList<AiLineRequest> Requests
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

    public async Task<IReadOnlyList<AiLineSuggestion?>> ParseAsync(AiLineRequest request, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        return await handler(request, cancellationToken);
    }

    /// <summary>Answers every request by filling request line 0 with butter (3 ss) and line 1 (if any) with salt to taste.</summary>
    public static FakeRecipeLineAi Helpful() => new(request =>
    {
        var answers = new List<AiLineSuggestion?> { new(0, "smør", 3, null, "ss", "Measured", "Main", false, null, null) };
        if (request.Lines.Count > 1)
        {
            answers.Add(new AiLineSuggestion(1, "salt", null, null, null, "ToTaste", "Seasoning", false, null, null));
        }

        return answers;
    });
}

internal sealed class FuncHandler(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        return await respond(request, body);
    }
}
