using System.Text.RegularExpressions;

namespace Foodplanner.Architecture.Tests;

/// <summary>
/// T-077 verifier: the BulkWriteAllowed entry for AiUsageLimiter.cs is per FILE, so the file-level rule alone would let a later
/// edit run ExecuteUpdate on any entity there. This pins the entry to the two counter sets it was granted for.
/// </summary>
public partial class AiUsageBulkWriteScopeTests
{
    private const string LimiterFile = "src/Infrastructure/Import/Ai/AiUsageLimiter.cs";

    [GeneratedRegex(@"\bExecute(Update|Delete)(Async)?\b")]
    private static partial Regex Bulk();

    [GeneratedRegex(@"db\.(\w+)")]
    private static partial Regex DbSet();

    [Fact]
    public void The_limiter_bulk_writes_only_the_two_ai_counter_sets_and_never_deletes()
    {
        var file = SourceScanner.Load("src").Single(f => f.Path == LimiterFile);
        Assert.True(TenantSourceRules.BulkWriteAllowed.ContainsKey(LimiterFile));

        var matches = Bulk().Matches(file.Code);
        Assert.NotEmpty(matches); // not vacuous
        foreach (Match match in matches)
        {
            Assert.Equal("Update", match.Groups[1].Value); // no ExecuteDelete
            var before = file.Code[..match.Index];
            var receiver = DbSet().Matches(before).Last().Groups[1].Value;
            Assert.Contains(receiver, new[] { "AiHouseholdUsages", "AiGlobalUsages" });
        }

        // No other file may borrow the exemption.
        Assert.Equal(
            [LimiterFile, TenantSourceRules.TenantResolutionFile],
            TenantSourceRules.BulkWriteAllowed.Keys.Order(StringComparer.Ordinal).ToArray());
    }
}
