using System.Net;
using Campfire.Core;

namespace Campfire.Tests;

public sealed class GuardTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.255.255.255")]
    [InlineData("127.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.255")]
    [InlineData("10.0.0.0")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.0")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.0")]
    [InlineData("192.168.255.255")]
    [InlineData("169.254.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.255.255")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:172.16.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:93.184.216.34")]
    [InlineData("::192.168.1.1")]
    [InlineData("::10.0.0.1")]
    [InlineData("::169.254.169.254")]
    [InlineData("::93.184.216.34")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("64:ff9b::a00:5")]
    [InlineData("64:ff9b:1::a00:1")]
    [InlineData("64:ff9b:1::808:808")]
    [InlineData("64:ff9b:1:ffff::1")]
    [InlineData("::ffff:0:169.254.169.254")]
    [InlineData("::ffff:0:a9fe:a9fe")]
    [InlineData("::ffff:0:127.0.0.1")]
    [InlineData("::ffff:0:192.168.0.1")]
    [InlineData("2002:a9fe:a9fe::")]
    [InlineData("2001::1")]
    [InlineData("::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:2::1")]
    [InlineData("not-an-ip")]
    [InlineData("")]
    public void Private_addresses_are_refused(string address) =>
        Assert.True(PrivateNetwork.IsPrivate(address), address);

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_addresses_are_allowed(string address) =>
        Assert.False(PrivateNetwork.IsPrivate(address), address);

    [Fact]
    public void Resolve_distinguishes_private_public_and_unresolvable_hosts()
    {
        var violation = Assert.Throws<ViolationException>(() => PrivateNetwork.Resolve("private.example.com", _ => [IPAddress.Parse("192.168.1.1")]));
        Assert.NotNull(violation);
        Assert.Equal("93.184.216.34", PrivateNetwork.Resolve("example.com", _ => [IPAddress.Parse("93.184.216.34")]).ToString());
        Assert.Equal("93.184.216.34", PrivateNetwork.Resolve("93.184.216.34", _ => []).ToString());
        Assert.Throws<UnresolvableException>(() => PrivateNetwork.Resolve("nxdomain.example.com", _ => []));
        Assert.Equal("8.8.8.8", PrivateNetwork.Resolve("mixed.example.com", _ => [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("8.8.8.8")]).ToString());
    }

    [Fact]
    public void Unfurl_encodes_quotes_and_rejects_a_private_host()
    {
        var image = "http://cdn.example/image.png?from=\" style=\"outline:9px solid red";
        var html = $"""
            <html><head>
              <meta property="og:title" content="A normal looking link">
              <meta property="og:description" content="Nothing to see here">
              <meta property="og:image" content='{image}'>
            </head></html>
            """;
        var preview = Unfurl.FromHtml("https://example.com/page", html);
        Assert.NotNull(preview);
        Assert.Equal("A normal looking link", preview.Title);
        Assert.Contains("&quot;", preview.Html);
        Assert.DoesNotContain("style=\"", preview.Html);
        Assert.False(Unfurl.ShouldFetch("https://cdn.example/photo.png"));
        Assert.Throws<ViolationException>(() => Unfurl.EnsurePublic("https://secret.example/page", _ => [IPAddress.Parse("127.0.0.1")]));
    }

    [Fact]
    public void Sanitizer_keeps_safe_markup_and_strips_scripts()
    {
        var safe = "<div>Hello <strong>bold</strong> <em>it</em> <del>gone</del> <a href=\"https://example.com/\">link</a><br>second line</div>";
        Assert.Equal(safe, HtmlText.Sanitize(safe, "campfire.test"));
        Assert.DoesNotContain("javascript:", HtmlText.Sanitize("<div><a href=\"javascript:alert(1)\">x</a></div>", null));
        Assert.Equal("<div><a>x</a></div>", HtmlText.Sanitize("<div><a href=\"javascript:alert(1)\">x</a></div>", null).Replace(" ", ""));
        Assert.DoesNotContain("onmouseover", HtmlText.Sanitize("<a href=\"/x\" onmouseover=\"alert(1)\">x</a>", null));
        Assert.Contains("href=\"/x\"", HtmlText.Sanitize("<a href=\"/x\" onmouseover=\"alert(1)\">x</a>", null));
        Assert.DoesNotContain("data:", HtmlText.Sanitize("<a href=\"data:text/html,pwned\">x</a>", null));
        Assert.Equal("Hello World", HtmlText.Sanitize("Hello <img src=\"https://evil.example/x.svg\">World", null));
        Assert.Contains("<s>struck</s>", HtmlText.Sanitize("<p>Hello <s>struck</s> <u>under</u></p>", null));
        Assert.Contains("<table>", HtmlText.Sanitize("<figure class=\"lexxy-content__table-wrapper\"><table><tbody><tr><td>Jason</td></tr></tbody></table></figure>", null));
        Assert.DoesNotContain("href=", HtmlText.Sanitize("<a href=\"https://203.0.113.5/secret\">x</a>", null));
        Assert.Contains("xn--n3h.example", HtmlText.Sanitize("<a href=\"https://xn--n3h.example/caf%C3%A9\">puny</a>", null));
        Assert.DoesNotContain("href=", HtmlText.Sanitize("<a href=\"https://campfire.test/rooms/1\">home</a>", "campfire.test"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/114.0", false)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15", true)]
    [InlineData("Mozilla/5.0 Chrome/119.0.0.0 Safari/537.36", false)]
    [InlineData("Mozilla/5.0 Chrome/120.0.0.0 Safari/537.36", true)]
    public void Browser_policy_matches_the_supported_versions(string? agent, bool allowed) =>
        Assert.Equal(allowed, BrowserPolicy.Allowed(agent));

    [Fact]
    public void Login_attempts_are_limited_to_ten_inside_three_minutes()
    {
        var time = new ManualTime();
        var limiter = new LoginRateLimiter(time);
        for (var attempt = 0; attempt < 10; attempt++)
            Assert.True(limiter.TryAcquire("david@37signals.com"));
        Assert.False(limiter.TryAcquire("david@37signals.com"));
        Assert.True(limiter.TryAcquire("other@37signals.com"));
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.True(limiter.TryAcquire("david@37signals.com"));
    }

    [Fact]
    public async Task Hot_path_summary_includes_the_comparison_fields()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"campfire-hot-{Guid.NewGuid():n}");
        var database = Path.Combine(directory, "campfire.sqlite");
        var json = await HotPaths.MeasureAsync(database);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        foreach (var name in new[] { "room", "messages", "sidebar", "search" })
        {
            var side = document.RootElement.GetProperty("results").GetProperty(name);
            Assert.True(side.GetProperty("milliseconds").GetDouble() >= 0);
            Assert.True(side.GetProperty("queries").GetDouble() >= 1);
            Assert.True(side.GetProperty("allocations").GetDouble() >= 0);
            Assert.True(side.GetProperty("round_medians_ms").GetArrayLength() >= 2);
        }

        Directory.Delete(directory, true);
    }
}
