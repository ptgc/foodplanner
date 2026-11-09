using Foodplanner.Domain.Imports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Foodplanner.Infrastructure.Import.Ai;

/// <summary>Runs the AI fallback after the rule-based parse (T-077). Returns the draft unchanged (with a status) whenever it cannot help.</summary>
public interface IAiImportAssistant
{
    /// <param name="matcher">The household's visible catalog (loaded through the tenant filter).</param>
    /// <param name="unitIdsByCode">The catalog units by code.</param>
    Task<ImportDraft> AssistAsync(
        ImportDraft draft, IngredientMatcher matcher, IReadOnlyDictionary<string, Guid> unitIdsByCode, CancellationToken cancellationToken);
}

/// <summary>
/// The fallback in order: off or no key means nothing happens and nothing leaves the server; no unresolved line means no call;
/// a usage limit means no call and the status <see cref="AiAssistStatus.LimitReached"/>; otherwise one batched call with
/// only the unresolved lines' original text. Any failure, or an answer that is not usable, keeps the rule-based lines
/// (<see cref="AiAssistStatus.Unavailable"/> for a failed call). Valid lines are merged by <see cref="AiSuggestionMerger"/> and
/// marked AI-suggested; invalid ones are dropped. The line text is never logged.
/// </summary>
public sealed class AiImportAssistant(
    IRecipeLineAi ai, IAiUsageLimiter limiter, IOptions<AiImportOptions> options, ILogger<AiImportAssistant> logger) : IAiImportAssistant
{
    public async Task<ImportDraft> AssistAsync(
        ImportDraft draft, IngredientMatcher matcher, IReadOnlyDictionary<string, Guid> unitIdsByCode, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.IsActive)
        {
            return draft;
        }

        var sent = AiSuggestionMerger.Unresolved(draft);
        if (sent.Count == 0)
        {
            return draft;
        }

        AiReservation reservation;
        try
        {
            reservation = await limiter.TryReserveAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The AI usage counter failed ({Error}); the rule-based import stands.", ex.GetType().Name);
            return draft with { AiAssist = AiAssistStatus.Unavailable };
        }

        if (reservation != AiReservation.Reserved)
        {
            logger.LogInformation("AI import assist skipped: {Limit}.", reservation);
            return draft with { AiAssist = AiAssistStatus.LimitReached };
        }

        var request = new AiLineRequest(
            [.. sent.Select(i => AiSuggestionMerger.RequestText(draft.Lines[i]))],
            [.. unitIdsByCode.Keys.Order(StringComparer.Ordinal)]);

        IReadOnlyList<AiLineSuggestion?> suggestions;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)));
        try
        {
            suggestions = await ai.ParseAsync(request, timeout.Token);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout, HTTP error, invalid JSON: never the content, only the kind of failure.
            logger.LogWarning("AI import assist failed ({Error}); the rule-based import stands.", ex.GetType().Name);
            return draft with { AiAssist = AiAssistStatus.Unavailable };
        }

        Guid? UnitId(string code) => unitIdsByCode.TryGetValue(code, out var id) ? id : null;
        var merged = AiSuggestionMerger.Merge(draft, sent, suggestions, matcher, UnitId);
        logger.LogInformation("AI import assist applied {Applied} of {Sent} lines ({Dropped} dropped).", merged.Applied, sent.Count, merged.Dropped);
        return merged.Draft with { AiAssist = AiAssistStatus.Applied };
    }
}
