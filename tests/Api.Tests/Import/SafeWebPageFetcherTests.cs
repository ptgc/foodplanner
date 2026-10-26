using System.IO.Compression;
using System.Net;
using System.Text;
using Foodplanner.Infrastructure.Import;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Tests.Import;

public class IpAddressRulesTests
{
    [Theory]
    // IPv4
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("169.254.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.20.5.5")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.88.99.1")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    // IPv6
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fe80::abcd:1234%1")]
    [InlineData("febf::1")]
    [InlineData("fec0::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("ff02::1")]
    [InlineData("ff0e::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001::1")]
    [InlineData("2001:1::1")]
    [InlineData("3fff::1")]
    [InlineData("100::1")]
    [InlineData("1::1")]
    // IPv4 inside IPv6
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("::ffff:0.0.0.0")]
    [InlineData("::127.0.0.1")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("64:ff9b:1::1")]
    [InlineData("2002:7f00:1::1")]
    [InlineData("2002:a9fe:a9fe::1")]
    [InlineData("2002:c0a8:101::1")]
    public void Private_loopback_link_local_and_special_addresses_are_not_public(string address) =>
        Assert.False(IpAddressRules.IsPublic(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("169.253.255.255")]
    [InlineData("169.255.0.1")]
    [InlineData("11.0.0.1")]
    [InlineData("126.255.255.255")]
    [InlineData("128.0.0.1")]
    [InlineData("192.169.0.1")]
    [InlineData("198.17.255.255")]
    [InlineData("198.20.0.1")]
    [InlineData("223.255.255.254")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:4001:81b::200e")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:808:808::1")]
    public void Public_unicast_addresses_are_allowed(string address) =>
        Assert.True(IpAddressRules.IsPublic(IPAddress.Parse(address)), address);

    [Fact]
    public void The_production_policy_allows_only_ports_80_and_443_and_public_addresses()
    {
        var policy = new PublicInternetPolicy();

        Assert.True(policy.IsPortAllowed(80));
        Assert.True(policy.IsPortAllowed(443));
        Assert.All(new[] { 0, 21, 22, 25, 8080, 8443, 3306, 5432, 6379, 65535 }, port => Assert.False(policy.IsPortAllowed(port)));
        Assert.False(policy.IsAddressAllowed(IPAddress.Loopback));
        Assert.True(policy.IsAddressAllowed(IPAddress.Parse("8.8.8.8")));
    }
}

public sealed class SafeWebPageFetcherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeResolver Dns(params (string Host, string Ip)[] extra) =>
        ImportTestServices.Resolver([("recipes.test", "127.0.0.1"), .. extra]);

    private static SafeWebPageFetcher Fetcher(
        FakeResolver resolver, IFetchPolicy? policy = null, int maxBytes = 1024 * 1024, int timeoutSeconds = 10, int maxRedirects = 3) =>
        new(resolver, policy ?? new LoopbackAllowedPolicy(), Options.Create(new RecipeImportOptions { MaxBytes = maxBytes, TimeoutSeconds = timeoutSeconds, MaxRedirects = maxRedirects }));

    private static TestHttpServer Html(string body = "<html>ok</html>", string contentType = "text/html; charset=utf-8") =>
        new((_, stream, ct) => TestHttpServer.Ok(stream, contentType, body, ct));

    // ---- Blocked addresses ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    public async Task A_name_that_resolves_to_a_blocked_address_is_refused_before_any_connection(string ip)
    {
        var resolver = ImportTestServices.Resolver(("evil.test", ip));
        using var fetcher = Fetcher(resolver, new PublicInternetPolicy());

        var result = await fetcher.FetchAsync("https://evil.test/recipe", Ct);

        Assert.Equal(FetchFailure.BlockedAddress, result.Failure);
        Assert.Equal(["evil.test"], resolver.Asked);
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.0.0.1:80/")]
    [InlineData("http://localhost/")] // resolved by name below: see the next test
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://192.168.0.1/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://2130706433/")] // 127.0.0.1 as one decimal number
    [InlineData("http://0x7f000001/")]
    [InlineData("http://0177.0.0.1/")]
    [InlineData("http://127.1/")]
    [InlineData("http://[::ffff:7f00:1]/")]
    [InlineData("http://metadata.google.internal/")] // not resolvable here: still refused (host not found), never fetched
    public async Task Blocked_addresses_given_as_literals_never_reach_the_network(string url)
    {
        var resolver = Dns(("localhost", "127.0.0.1"), ("metadata.google.internal", "169.254.169.254"));
        using var fetcher = Fetcher(resolver, new PublicInternetPolicy());

        var result = await fetcher.FetchAsync(url, Ct);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failure, new FetchFailure?[] { FetchFailure.InvalidUrl, FetchFailure.BlockedAddress, FetchFailure.NetworkError });
    }

    [Fact]
    public async Task Localhost_resolving_to_loopback_is_blocked_by_name()
    {
        using var fetcher = Fetcher(Dns(("localhost", "127.0.0.1")), new PublicInternetPolicy());

        var result = await fetcher.FetchAsync("http://localhost/", Ct);

        Assert.Equal(FetchFailure.BlockedAddress, result.Failure);
    }

    [Fact]
    public async Task One_blocked_address_in_a_mixed_answer_refuses_the_whole_answer()
    {
        var resolver = new FakeResolver(new() { ["mixed.test"] = [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.1")] });
        using var fetcher = Fetcher(resolver, new PublicInternetPolicy());

        var result = await fetcher.FetchAsync("http://mixed.test/", Ct);

        Assert.Equal(FetchFailure.BlockedAddress, result.Failure);
    }

    [Fact]
    public async Task A_host_that_does_not_resolve_is_a_network_error()
    {
        using var fetcher = Fetcher(Dns());

        var result = await fetcher.FetchAsync("http://nowhere.test/", Ct);

        Assert.Equal(FetchFailure.NetworkError, result.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("recipes.test/recipe")]
    [InlineData("//recipes.test/recipe")]
    [InlineData("/relative/path")]
    [InlineData("ftp://recipes.test/recipe")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://recipes.test/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<h1>x</h1>")]
    [InlineData("ws://recipes.test/")]
    [InlineData("http://user:password@recipes.test/")]
    [InlineData("http://user@recipes.test/")]
    [InlineData("http://")]
    public async Task Only_absolute_http_and_https_urls_without_credentials_are_accepted(string url)
    {
        var resolver = Dns();
        using var fetcher = Fetcher(resolver, new PublicInternetPolicy());

        var result = await fetcher.FetchAsync(url, Ct);

        Assert.Equal(FetchFailure.InvalidUrl, result.Failure);
        Assert.Empty(resolver.Asked);
    }

    [Theory]
    [InlineData("http://recipes.test:8080/")]
    [InlineData("https://recipes.test:8443/")]
    [InlineData("http://recipes.test:22/")]
    [InlineData("http://recipes.test:25/")]
    [InlineData("http://recipes.test:6379/")]
    [InlineData("http://recipes.test:1/")]
    public async Task Only_ports_80_and_443_are_allowed(string url)
    {
        var resolver = Dns();
        using var fetcher = Fetcher(resolver, new PublicInternetPolicy());

        var result = await fetcher.FetchAsync(url, Ct);

        Assert.Equal(FetchFailure.InvalidUrl, result.Failure);
        Assert.Empty(resolver.Asked);
    }

    [Fact]
    public async Task A_very_long_url_is_refused()
    {
        using var fetcher = Fetcher(Dns(), new PublicInternetPolicy());

        var result = await fetcher.FetchAsync("http://recipes.test/" + new string('a', SafeWebPageFetcher.MaxUrlLength), Ct);

        Assert.Equal(FetchFailure.InvalidUrl, result.Failure);
    }

    // ---- Redirects --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.5/admin")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://[::ffff:10.0.0.5]/")]
    [InlineData("ftp://recipes.test/file")]
    [InlineData("file:///etc/passwd")]
    public async Task A_redirect_to_a_blocked_literal_or_scheme_is_refused(string target)
    {
        await using var server = new TestHttpServer((_, stream, ct) => TestHttpServer.Redirect(stream, target, ct));
        using var fetcher = Fetcher(Dns());

        var result = await fetcher.FetchAsync(server.Url(), Ct);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failure, new FetchFailure?[] { FetchFailure.InvalidUrl, FetchFailure.BlockedAddress });
        Assert.Single(server.Seen); // the target was never requested
    }

    [Fact]
    public async Task A_redirect_to_a_name_that_resolves_to_a_private_address_is_refused()
    {
        var resolver = Dns(("internal.test", "10.0.0.5"));
        await using var server = new TestHttpServer((_, stream, ct) => TestHttpServer.Redirect(stream, "http://internal.test/secret", ct));
        using var fetcher = Fetcher(resolver);

        var result = await fetcher.FetchAsync(server.Url(), Ct);

        Assert.Equal(FetchFailure.BlockedAddress, result.Failure);
        Assert.Contains("internal.test", resolver.Asked);
    }

    [Fact]
    public async Task A_name_whose_answer_changes_between_requests_is_vetted_again_on_every_connection()
    {
        // DNS rebinding: the first answer is a safe address, the next one is the metadata address. Each connection resolves and
        // vets again and connects to the address it vetted, so the second hop is refused.
        var resolver = Dns();
        resolver.Override = (host, count) => host == "rebind.test" ? [count == 1 ? IPAddress.Loopback : IPAddress.Parse("169.254.169.254")] : null;
        await using var server = new TestHttpServer((request, stream, ct) => request.Path == "/start"
            ? TestHttpServer.Redirect(stream, "/next", ct) // same name, new connection
            : TestHttpServer.Ok(stream, "text/html", "x", ct));
        using var fetcher = Fetcher(resolver);

        var result = await fetcher.FetchAsync($"http://rebind.test:{server.Port}/start", Ct);

        Assert.Equal(FetchFailure.BlockedAddress, result.Failure);
        Assert.Equal(2, resolver.Asked.Count(h => h == "rebind.test")); // asked again for the second connection
        Assert.Single(server.Seen); // the second connection was never made
    }

    [Fact]
    public async Task Up_to_three_redirects_are_followed_and_relative_locations_resolve()
    {
        await using var server = new TestHttpServer((request, stream, ct) => request.Path switch
        {
            "/a" => TestHttpServer.Redirect(stream, "/b", ct, "301 Moved Permanently"),
            "/b" => TestHttpServer.Redirect(stream, "c", ct, "307 Temporary Redirect"),
            "/c" => TestHttpServer.Redirect(stream, "/d", ct, "308 Permanent Redirect"),
            _ => TestHttpServer.Ok(stream, "text/html", "<p>arrived</p>", ct),
        });
        using var fetcher = Fetcher(Dns());

        var result = await fetcher.FetchAsync(server.Url("/a"), Ct);

        Assert.True(result.Succeeded);
        Assert.Equal("<p>arrived</p>", result.Content);
        Assert.Equal("/d", result.FinalUrl!.AbsolutePath);
        Assert.Equal(4, server.Seen.Count);
    }

    [Fact]
    public async Task A_fourth_redirect_is_too_many()
    {
        await using var server = new TestHttpServer((request, stream, ct) =>
            TestHttpServer.Redirect(stream, "/hop" + (int.TryParse(request.Path.AsSpan(4), out var n) ? n + 1 : 1), ct));
        using var fetcher = Fetcher(Dns());

        var result = await fetcher.FetchAsync(server.Url("/hop0"), Ct);

        Assert.Equal(FetchFailure.TooManyRedirects, result.Failure);
        Assert.Equal(4, server.Seen.Count); // the first request and three redirects
    }

    [Fact]
    public async Task A_redirect_without_a_location_is_refused()
    {
        await using var server = new TestHttpServer((_, stream, ct) =>
            TestHttpServer.WriteAsync(stream, "302 Found", [("Content-Length", "0")], [], ct));
        using var fetcher = Fetcher(Dns());

        var result = await fetcher.FetchAsync(server.Url(), Ct);

        Assert.Equal(FetchFailure.InvalidUrl, result.Failure);
    }

    // ---- Size and time ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_declared_length_over_the_limit_is_refused_without_reading_the_body()
    {
        await using var server = new TestHttpServer((_, stream, ct) =>
            TestHttpServer.WriteAsync(stream, "200 OK", [("Content-Type", "text/html"), ("Content-Length", "5000000")], Encoding.ASCII.GetBytes("<html>"), ct));
        using var fetcher = Fetcher(Dns(), maxBytes: 1000);

        var result = await fetcher.FetchAsync(server.Url(), Ct);

        Assert.Equal(FetchFailure.TooLarge, result.Failure);
    }

    [Fact]
    public async Task A_body_that_keeps_coming_without_a_length_stops_at_the_limit()
    {
        await using var server = new TestHttpServer(async (_, stream, ct) =>
        {
            await TestHttpServer.WriteAsync(stream, "200 OK", [("Content-Type", "text/html")], [], ct);
            var chunk = new byte[8192];
            Array.Fill(chunk, (byte)'a');
            for (var i = 0; i < 2000; i++)
            {
                await stream.WriteAsync(chunk, ct);
            }
        });
        using var fetcher = Fetcher(Dns(), maxBytes: 100_000);

        var result = await fetcher.FetchAsync(server.Url(), Ct);

        Assert.Equal(FetchFailure.TooLarge, result.Failure);
    }

    [Fact]
    public async Task A_body_exactly_at_the_limit_is_accepted_and_one_byte_more_is_not()
    {
        await using var exact = Html(new string('a', 1000));
        await using var over = Html(new string('a', 1001));
        using var fetcher = Fetcher(Dns(), maxBytes: 1000);

        Assert.True((await fetcher.FetchAsync(exact.Url(), Ct)).Succeeded);
        Assert.Equal(FetchFailure.TooLarge, (await fetcher.FetchAsync(over.Url(), Ct)).Failure);
    }

    [Fact]
    public async Task A_compressed_body_that_inflates_past_the_limit_is_refused()
    {
        // A decompression bomb: 5 MB of zeros gzips to a few KB, so the wire size is small and only the inflated size shows it.
        byte[] bomb;
        using (var buffer = new MemoryStream())
        {
            await using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                await gzip.WriteAsync(new byte[5 * 1024 * 1024], Ct);
            }

            bomb = buffer.ToArray();
        }

        Assert.True(bomb.Length < 100_000);
        await using var server = new TestHttpServer((_, stream, ct) => TestHttpServer.WriteAsync(
            stream, "200 OK", [("Content-Type", "text/html"), ("Content-Encoding", "gzip"), ("Content-Length", bomb.Length.ToString())], bomb, ct));
        using var fetcher = Fetcher(Dns(), maxBytes: 1024 * 1024);

        var result = await fetcher.FetchAsync(server.Url(), Ct);

        Assert.Equal(FetchFailure.TooLarge, result.Failure);
    }

    [Fact]
    public async Task A_gzipped_page_within_the_limit_is_decoded()
    {
        byte[] gzipped;
        using (var buffer = new MemoryStream())
        {
            await using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                await gzip.WriteAsync(Encoding.UTF8.GetBytes("<p>blåbærsuppe</p>"), Ct);
            }

            gzipped = buffer.ToArray();
        }

        await using var server = new TestHttpServer((_, stream, ct) => TestHttpServer.WriteAsync(
            stream, "200 OK", [("Content-Type", "text/html; charset=utf-8"), ("Content-Encoding", "gzip"), ("Content-Length", gzipped.Length.ToString())], gzipped, ct));
        using var fetcher = Fetcher(Dns());

        var result = await fetcher.FetchAsync(server.Url(), Ct);

        Assert.Equal("<p>blåbærsuppe</p>", result.Content);
    }

    [Fact]
    public async Task A_server_that_trickles_the_body_hits_the_total_timeout()
    {
        await using var server = new TestHttpServer(async (_, stream, ct) =>
        {
            await TestHttpServer.WriteAsync(stream, "200 OK", [("Content-Type", "text/html"), ("Content-Length", "1000")], [], ct);
            for (var i = 0; i < 100; i++)
            {
                await stream.WriteAsync("x"u8.ToArray(), ct);
                await stream.FlushAsync(ct);
                await Task.Delay(200, ct);
            }
        });
        using var fetcher = Fetcher(Dns(), timeoutSeconds: 1);
        var started = DateTime.UtcNow;

        var result = await fetcher.FetchAsync(server.Url(), Ct);

        Assert.Equal(FetchFailure.Timeout, result.Failure);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task A_server_that_never_answers_hits_the_total_timeout()
    {
        await using var server = new TestHttpServer((_, _, ct) => Task.Delay(TimeSpan.FromSeconds(30), ct));
        using var fetcher = Fetcher(Dns(), timeoutSeconds: 1);

        var result = await fetcher.FetchAsync(server.Url(), Ct);

        Assert.Equal(FetchFailure.Timeout, result.Failure);
    }

    [Fact]
    public async Task The_timeout_covers_the_whole_chain_of_redirects()
    {
        await using var server = new TestHttpServer(async (request, stream, ct) =>
        {
            await Task.Delay(700, ct);
            await TestHttpServer.Redirect(stream, "/next" + request.Path.Length, ct);
        });
        using var fetcher = Fetcher(Dns(), timeoutSeconds: 2, maxRedirects: 10);

        var result = await fetcher.FetchAsync(server.Url("/start"), Ct);

        Assert.Equal(FetchFailure.Timeout, result.Failure);
    }

    [Fact]
    public async Task A_caller_that_cancels_gets_a_cancellation_not_a_timeout()
    {
        await using var server = new TestHttpServer((_, _, ct) => Task.Delay(TimeSpan.FromSeconds(30), ct));
        using var fetcher = Fetcher(Dns());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(300);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.FetchAsync(server.Url(), cts.Token));
    }

    // ---- Content ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/json")]
    [InlineData("application/pdf")]
    [InlineData("image/png")]
    [InlineData("application/octet-stream")]
    [InlineData("text/xml")]
    [InlineData("application/javascript")]
    public async Task Other_content_types_are_refused(string contentType)
    {
        await using var server = Html("body", contentType);
        using var fetcher = Fetcher(Dns());

        var result = await fetcher.FetchAsync(server.Url(), Ct);

        Assert.Equal(FetchFailure.UnsupportedContentType, result.Failure);
    }

    [Fact]
    public async Task A_response_without_a_content_type_is_refused()
    {
        await using var server = new TestHttpServer((_, stream, ct) =>
            TestHttpServer.WriteAsync(stream, "200 OK", [("Content-Length", "2")], "hi"u8.ToArray(), ct));
        using var fetcher = Fetcher(Dns());

        var result = await fetcher.FetchAsync(server.Url(), Ct);

        Assert.Equal(FetchFailure.UnsupportedContentType, result.Failure);
    }

    [Theory]
    [InlineData("text/html", "text/html")]
    [InlineData("TEXT/HTML; charset=UTF-8", "text/html")]
    [InlineData("application/ld+json", "application/ld+json")]
    [InlineData("application/ld+json; charset=utf-8", "application/ld+json")]
    public async Task Html_and_json_ld_are_read(string contentType, string expected)
    {
        await using var server = Html("{\"a\":1}", contentType);
        using var fetcher = Fetcher(Dns());

        var result = await fetcher.FetchAsync(server.Url(), Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.ContentType);
        Assert.Equal("{\"a\":1}", result.Content);
    }

    [Fact]
    public async Task The_charset_of_the_response_is_honoured()
    {
        var latin1 = Encoding.Latin1.GetBytes("<p>blåbær</p>");
        await using var server = new TestHttpServer((_, stream, ct) => TestHttpServer.WriteAsync(
            stream, "200 OK", [("Content-Type", "text/html; charset=iso-8859-1"), ("Content-Length", latin1.Length.ToString())], latin1, ct));
        using var fetcher = Fetcher(Dns());

        var result = await fetcher.FetchAsync(server.Url(), Ct);

        Assert.Equal("<p>blåbær</p>", result.Content);
    }

    [Theory]
    [InlineData("404 Not Found", 404)]
    [InlineData("403 Forbidden", 403)]
    [InlineData("500 Internal Server Error", 500)]
    [InlineData("503 Service Unavailable", 503)]
    public async Task An_error_status_is_a_failure_with_the_status(string status, int code)
    {
        await using var server = new TestHttpServer((_, stream, ct) =>
            TestHttpServer.WriteAsync(stream, status, [("Content-Type", "text/html"), ("Content-Length", "4")], "nope"u8.ToArray(), ct));
        using var fetcher = Fetcher(Dns());

        var result = await fetcher.FetchAsync(server.Url(), Ct);

        Assert.Equal(FetchFailure.HttpStatus, result.Failure);
        Assert.Equal(code, result.StatusCode);
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task Something_that_is_not_http_is_a_network_error()
    {
        await using var server = new TestHttpServer((_, stream, ct) => stream.WriteAsync("SSH-2.0-OpenSSH_9.0\r\n"u8.ToArray(), ct).AsTask());
        using var fetcher = Fetcher(Dns());

        var result = await fetcher.FetchAsync(server.Url(), Ct);

        Assert.Equal(FetchFailure.NetworkError, result.Failure);
    }

    // ---- What is sent -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_request_carries_a_fixed_user_agent_and_no_cookies_or_credentials_even_across_redirects()
    {
        await using var server = new TestHttpServer((request, stream, ct) => request.Path == "/start"
            ? TestHttpServer.Redirect(stream, "/final", ct) // sets a cookie
            : TestHttpServer.Ok(stream, "text/html", "ok", ct));
        using var fetcher = Fetcher(Dns());

        var result = await fetcher.FetchAsync(server.Url("/start"), Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(2, server.Seen.Count);
        foreach (var request in server.Seen)
        {
            Assert.Equal(SafeWebPageFetcher.UserAgent, request.Headers["User-Agent"]);
            Assert.DoesNotContain("Cookie", request.Headers.Keys);
            Assert.DoesNotContain("Authorization", request.Headers.Keys);
            Assert.DoesNotContain("Proxy-Authorization", request.Headers.Keys);
            Assert.Equal($"recipes.test:{server.Port}", request.Headers["Host"]);
        }
    }

    [Fact]
    public async Task The_connection_goes_to_the_address_that_was_vetted_for_the_name()
    {
        // recipes.test is not a real name: the server is reached only because the fetcher connected to the resolver's answer.
        var resolver = Dns();
        await using var server = Html();
        using var fetcher = Fetcher(resolver);

        var result = await fetcher.FetchAsync(server.Url(), Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(["recipes.test"], resolver.Asked);
    }
}
