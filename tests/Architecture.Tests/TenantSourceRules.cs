using System.Text.RegularExpressions;

namespace Foodplanner.Architecture.Tests;

/// <summary>
/// The tenancy source rules as pure functions over <see cref="SourceFile"/>, so the tests can run them on the real
/// solution and on deliberately bad snippets (mutants) to prove they bite.
/// </summary>
public static partial class TenantSourceRules
{
    public const string TenantResolutionFile = "src/Infrastructure/Persistence/Tenancy/TenantResolution.cs";

    /// <summary>
    /// The complete list of files that may use a tenant bypass, and which one. Production code: only the
    /// ITenantResolution implementation (Q-006). Test support is listed with a reason; add a line here, with a reason,
    /// rather than calling a bypass from a test or feature file. Unnamed <c>IgnoreQueryFilters()</c> is never allowed,
    /// not even here.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, BypassAllowance> Allowed = new Dictionary<string, BypassAllowance>
    {
        [TenantResolutionFile] = new(
            ["IgnoreQueryFilters", "ForHousehold"],
            "The ITenantResolution implementation: the single sanctioned bypass (Q-006)."),
        ["tests/Api.Tests/Persistence/TenancyWriteGuardTests.cs"] = new(
            ["ForHousehold"],
            "Test support: proves what the write scope does and does not permit, so it must open scopes itself."),
    };

    public sealed record BypassAllowance(string[] Symbols, string Reason);

    public sealed record Finding(string File, int Line, string Message)
    {
        public override string ToString() => $"{File}:{Line} {Message}";
    }

    [GeneratedRegex(@"\bIgnoreQueryFilters\b")]
    private static partial Regex IgnoreFilters();

    [GeneratedRegex(@"\bForHousehold\b")]
    private static partial Regex WriteScope();

    [GeneratedRegex(@"\GIgnoreQueryFilters\s*\(\s*[^)\s]")]
    private static partial Regex NamedArguments();

    [GeneratedRegex(@"\bSetProperty\s*\(")]
    private static partial Regex SetPropertyCall();

    [GeneratedRegex(@"\bHouseholdId\b")]
    private static partial Regex HouseholdIdName();

    [GeneratedRegex(@"\b(ExecuteSql\w*|SqlQuery\w*|FromSql\w*|GetDbConnection|DbConnection|DbCommand|SqlConnection|SqlCommand|CreateCommand)\b")]
    private static partial Regex RawSql();

    /// <summary>Criterion 3: bypasses only where <see cref="Allowed"/> says, and never unnamed.</summary>
    public static List<Finding> Bypasses(IEnumerable<SourceFile> files)
    {
        var findings = new List<Finding>();
        foreach (var file in files)
        {
            Allowed.TryGetValue(file.Path, out var allowance);

            foreach (Match match in IgnoreFilters().Matches(file.Code))
            {
                var line = file.LineOf(match.Index);
                if (!NamedArguments().IsMatch(file.Code, match.Index))
                {
                    findings.Add(new(file.Path, line, "IgnoreQueryFilters without named filters ignores every filter; pass the filter name(s) to bypass, e.g. IgnoreQueryFilters([AppDbContext.TenantFilterName])."));
                }
                else if (allowance is null || !allowance.Symbols.Contains("IgnoreQueryFilters"))
                {
                    findings.Add(new(file.Path, line, "IgnoreQueryFilters is only for the ITenantResolution implementation (Q-006). Add a method to ITenantResolution instead."));
                }
            }

            foreach (Match match in WriteScope().Matches(file.Code))
            {
                if (IsDeclaration(file.Code, match.Index))
                {
                    continue;
                }

                if (allowance is null || !allowance.Symbols.Contains("ForHousehold"))
                {
                    findings.Add(new(file.Path, file.LineOf(match.Index), "ITenantWriteScope.ForHousehold is only for the ITenantResolution implementation (Q-006). Rename an unrelated member that is not the write scope."));
                }
            }
        }

        return findings;
    }

    // A declaration of ITenantWriteScope.ForHousehold (or of an implementation) is not a call.
    private static bool IsDeclaration(string code, int index)
    {
        var before = code[..index].TrimEnd();
        return before.EndsWith("IDisposable", StringComparison.Ordinal);
    }

    /// <summary>Allow-list entries that no longer match anything are stale and must be removed.</summary>
    public static List<string> StaleAllowances(IEnumerable<SourceFile> files)
    {
        var byPath = files.ToDictionary(f => f.Path);
        var stale = new List<string>();
        foreach (var (path, allowance) in Allowed)
        {
            if (!byPath.TryGetValue(path, out var file))
            {
                stale.Add($"{path}: file does not exist");
                continue;
            }

            foreach (var symbol in allowance.Symbols)
            {
                if (!Regex.IsMatch(file.Code, $@"\b{symbol}\b"))
                {
                    stale.Add($"{path}: no longer uses {symbol}");
                }
            }
        }

        return stale;
    }

    /// <summary>
    /// Gap a: <c>ExecuteUpdate</c> setting HouseholdId moves rows between households (and skips UpdatedAt), and no
    /// SaveChanges guard sees it. Any <c>SetProperty</c> whose arguments mention HouseholdId, as an identifier or as a
    /// column-name string, is rejected. A name assembled at run time cannot be seen; code review has to catch that.
    /// </summary>
    public static List<Finding> HouseholdIdSetters(IEnumerable<SourceFile> files)
    {
        var findings = new List<Finding>();
        foreach (var file in files)
        {
            foreach (Match call in SetPropertyCall().Matches(file.Code))
            {
                var open = call.Index + call.Length - 1;
                var close = MatchingParenthesis(file.CodeWithStrings, open);
                var arguments = file.CodeWithStrings[open..(close + 1)];
                if (HouseholdIdName().IsMatch(arguments))
                {
                    findings.Add(new(file.Path, file.LineOf(call.Index), "SetProperty must not set HouseholdId: it moves rows between households and bypasses the write guard."));
                }
            }
        }

        return findings;
    }

    private static int MatchingParenthesis(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            depth += text[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return i;
            }
        }

        return text.Length - 1;
    }

    /// <summary>
    /// Gap b: raw SQL and raw ADO.NET bypass both the query filter and the write guard. Not allowed in production code
    /// (the caller excludes Migrations, which are generated). Tests may use them freely.
    /// </summary>
    public static List<Finding> RawSqlUse(IEnumerable<SourceFile> files)
    {
        var findings = new List<Finding>();
        foreach (var file in files)
        {
            foreach (Match match in RawSql().Matches(file.Code))
            {
                findings.Add(new(file.Path, file.LineOf(match.Index), $"{match.Value}: raw SQL bypasses the tenant filter and write guard. Use LINQ, or a reviewed migration."));
            }
        }

        return findings;
    }
}
