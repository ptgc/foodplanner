namespace Foodplanner.Architecture.Tests;

/// <summary>
/// All outbound HTTP from user-supplied URLs goes through the SSRF-safe fetcher (T-042, CLAUDE.md rule 7). A new
/// <c>HttpClient</c>, handler, socket or <c>WebRequest</c> anywhere else in <c>src</c> would skip its address checks, so it needs
/// a reasoned entry in <see cref="Allowed"/> (for example a fixed-host provider client such as Oda, which never takes a
/// URL from a user) or must use <c>IWebPageFetcher</c>.
/// </summary>
public class OutboundHttpRulesTests
{
    private static readonly string[] Names =
    [
        "HttpClient", "IHttpClientFactory", "HttpMessageHandler", "SocketsHttpHandler", "HttpClientHandler", "WebRequest", "HttpWebRequest",
        "WebClient", "TcpClient", "Socket", "AddHttpClient", "UdpClient", "SslStream", "QuicConnection", "NetworkStream", "Dns",
    ];

    /// <summary>File (solution-relative) and the reason it may use outbound networking.</summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        ["src/Infrastructure/Import/SafeWebPageFetcher.cs"] = "The SSRF-safe fetcher itself: resolves, vets and connects (T-042).",
        ["src/Infrastructure/Import/WebPageFetching.cs"] = "The fetcher's DNS resolver seam (DnsHostResolver); its answers are vetted by the fetcher before any connection.",
    };

    [Fact]
    public void Outbound_http_and_sockets_are_used_only_in_allowed_files()
    {
        var offenders = new List<string>();
        foreach (var file in SourceScanner.Load("src"))
        {
            if (Allowed.ContainsKey(file.Path))
            {
                continue;
            }

            foreach (var name in Names)
            {
                var at = file.Code.IndexOf(name, StringComparison.Ordinal);
                while (at >= 0)
                {
                    var before = at == 0 ? ' ' : file.Code[at - 1];
                    var after = at + name.Length < file.Code.Length ? file.Code[at + name.Length] : ' ';
                    if (!char.IsLetterOrDigit(before) && before != '_' && !char.IsLetterOrDigit(after) && after != '_')
                    {
                        offenders.Add($"{file.Path}:{file.LineOf(at)} uses {name}");
                    }

                    at = file.Code.IndexOf(name, at + name.Length, StringComparison.Ordinal);
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void PublicInternetPolicy_is_the_only_fetch_policy_in_src()
    {
        // A second IFetchPolicy (for example one that allows loopback) must never ship; tests define their own in tests/.
        var implementations = new List<string>();
        foreach (var file in SourceScanner.Load("src"))
        {
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                file.Code, @"\b(?:class|record|struct)\s+(\w+)[^{;=]*:[^{;=]*\bIFetchPolicy\b"))
            {
                implementations.Add(match.Groups[1].Value);
            }
        }

        Assert.Equal(["PublicInternetPolicy"], implementations);
    }

    [Fact]
    public void The_allowed_file_exists_and_really_uses_networking_so_the_rule_is_not_vacuous()
    {
        var files = SourceScanner.Load("src");
        Assert.All(Allowed.Keys, path => Assert.Contains(files, f => f.Path == path));
        var fetcher = files.Single(f => f.Path == "src/Infrastructure/Import/SafeWebPageFetcher.cs");
        Assert.Contains("SocketsHttpHandler", fetcher.Code, StringComparison.Ordinal);
        Assert.Contains("ConnectCallback", fetcher.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scanner_flags_a_stray_http_client_in_code_but_not_in_comments_or_strings()
    {
        var file = SourceFile.FromText("x.cs", "// new HttpClient()\nvar s = \"HttpClient\";\nvar c = new HttpClient();\n");

        // Only the use on line 3 is code: the comment and the string literal are blanked.
        Assert.Equal(3, file.LineOf(file.Code.IndexOf("HttpClient", StringComparison.Ordinal)));
    }
}
