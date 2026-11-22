using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Imports;

/// <summary>What an AI call was for. Only the photo import writes rows today (T-118); T-077's line help may later.</summary>
public enum AiCallKind
{
    PhotoImport,
}

/// <summary>How an AI call ended. Stored as text.</summary>
public enum AiCallOutcome
{
    Ok,
    NotARecipe,
    Unreadable,
    Refused,
    MaxTokens,
    Timeout,
    Failed,
}

/// <summary>
/// One row per call to Claude (T-118), for the usage and cost metrics of T-121: who (the household), what (kind, model, effort,
/// skill version, number of photos), how much (tokens, null when the call produced no usage) and how it ended. By design it holds
/// no user id, no photo, no text and no recipe content. <see cref="ImportId"/> is a plain id, not a foreign key, like
/// <c>RecipeImport.SavedRecipeId</c>. Rows are written once after the call and never updated by request code.
/// HouseholdId is assigned by Infrastructure.
/// </summary>
public sealed class AiCallLog : Entity, ITenantEntity
{
    public const int MaxModelLength = 100;
    public const int MaxEffortLength = 20;
    public const int MaxStopReasonLength = 40;

    private AiCallLog()
    {
    }

    public AiCallLog(
        AiCallKind kind, string model, string effort, int skillVersion, int photoCount, int? inputTokens, int? outputTokens,
        string? stopReason, AiCallOutcome outcome, int durationMs, Guid? importId)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        Model = Cut(model, MaxModelLength);
        Effort = Cut(effort, MaxEffortLength);
        SkillVersion = skillVersion;
        PhotoCount = Math.Max(0, photoCount);
        InputTokens = inputTokens is < 0 ? null : inputTokens;
        OutputTokens = outputTokens is < 0 ? null : outputTokens;
        StopReason = stopReason is null ? null : Cut(stopReason, MaxStopReasonLength);
        Outcome = outcome;
        DurationMs = Math.Max(0, durationMs);
        ImportId = importId;
    }

    public Guid HouseholdId { get; private set; }

    public AiCallKind Kind { get; private set; }

    public string Model { get; private set; } = string.Empty;

    public string Effort { get; private set; } = string.Empty;

    public int SkillVersion { get; private set; }

    public int PhotoCount { get; private set; }

    public int? InputTokens { get; private set; }

    public int? OutputTokens { get; private set; }

    public string? StopReason { get; private set; }

    public AiCallOutcome Outcome { get; private set; }

    public int DurationMs { get; private set; }

    public Guid? ImportId { get; private set; }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
