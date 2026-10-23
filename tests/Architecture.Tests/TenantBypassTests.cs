using System.Text.RegularExpressions;

namespace Foodplanner.Architecture.Tests;

/// <summary>
/// Q-006: ITenantResolution's implementation is the only code that bypasses the tenant filter or opens a tenant write
/// scope. T-012 delivers the enforcing, solution-wide version; this scans the production sources of T-011.
/// </summary>
public partial class TenantBypassTests
{
    private const string AllowedFile = "src/Infrastructure/Persistence/Tenancy/TenantResolution.cs";

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Foodplanner.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Solution root not found.");
    }

    [GeneratedRegex(@"\bIgnoreQueryFilters\s*\(|\.\s*ForHousehold\s*\(")]
    private static partial Regex Bypass();

    private static IEnumerable<(string File, int Line, string Text)> Occurrences(string root, string source)
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, source), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) && Bypass().IsMatch(lines[i]))
                {
                    yield return (relative, i + 1, lines[i].Trim());
                }
            }
        }
    }

    [Fact]
    public void Tenant_resolution_is_the_only_bypass()
    {
        var root = SolutionRoot();

        var all = Occurrences(root, "src").ToList();

        Assert.Contains(all, o => o.File == AllowedFile && o.Text.Contains("IgnoreQueryFilters", StringComparison.Ordinal)); // not vacuous
        Assert.Contains(all, o => o.File == AllowedFile && o.Text.Contains("ForHousehold", StringComparison.Ordinal));
        var outside = all.Where(o => o.File != AllowedFile).Select(o => $"{o.File}:{o.Line} {o.Text}").ToList();
        Assert.Empty(outside);
    }
}
