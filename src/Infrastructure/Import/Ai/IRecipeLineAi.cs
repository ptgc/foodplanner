using Foodplanner.Domain.Imports;

namespace Foodplanner.Infrastructure.Import.Ai;

/// <summary>
/// What leaves the server (T-077, Q-028): the original text of the unresolved ingredient lines and the catalog unit codes (global
/// data). No page text, no user, household, recipe or ingredient ids, no email.
/// </summary>
public sealed record AiLineRequest(IReadOnlyList<string> Lines, IReadOnlyList<string> UnitCodes);

/// <summary>Any failure of the AI call: timeout, HTTP error, an answer that is not the expected JSON. Never carries line text.</summary>
public sealed class AiUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The LLM behind the import fallback. One call carries all unresolved lines of one import. The suggestions are untrusted: the
/// caller validates them (<see cref="AiSuggestionMerger"/>). Implementations throw <see cref="AiUnavailableException"/> (or let a
/// timeout cancellation through) on any failure. Tests use a fake; <c>verify.sh</c> never calls the real API.
/// </summary>
public interface IRecipeLineAi
{
    Task<IReadOnlyList<AiLineSuggestion?>> ParseAsync(AiLineRequest request, CancellationToken cancellationToken);
}
