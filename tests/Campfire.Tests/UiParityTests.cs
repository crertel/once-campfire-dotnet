using System.Net;
using System.Text.RegularExpressions;

namespace Campfire.Tests;

public sealed class UiParityTests
{
    [Fact]
    public async Task Pages_use_the_rails_chrome_classes_and_stylesheets()
    {
        using var factory = new CampfireFactory();
        using var client = HttpFlow.Client(factory);

        using var setup = await client.GetAsync("/");
        var setupHtml = await setup.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        AssertChrome(setupHtml);
        Assert.Contains("class=\"signup", setupHtml);
        Assert.Contains("class=\"nametag", setupHtml);
        Assert.Contains("nametag__lanyard", setupHtml);
        Assert.Contains("Set up Campfire", setupHtml);
        Assert.Contains("name=\"authenticity_token\"", setupHtml);
        Assert.Contains("user[name]", setupHtml);
        Assert.Contains("user[email_address]", setupHtml);
        Assert.Contains("user[password]", setupHtml);
        Assert.Contains("/assets/images/lanyard.svg", setupHtml);
        Assert.Contains("/assets/images/globe.svg", setupHtml);
        Assert.Contains(">Translate<", setupHtml);
        Assert.DoesNotContain("Georgia", setupHtml);
        Assert.DoesNotContain("--ember", setupHtml);

        var stylesheet = Regex.Match(setupHtml, "<link rel=\"stylesheet\" href=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(stylesheet));
        using var cssResponse = await client.GetAsync(stylesheet);
        var css = await cssResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, cssResponse.StatusCode);
        Assert.Contains("text/css", cssResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains("--color-bg", css);
        Assert.Contains("#main-content", css);
        Assert.Contains("grid-template-areas", css);
        Assert.Contains("-apple-system", css);
        Assert.Contains("url(/assets/images/cancel.svg)", css);

        using var lanyard = await client.GetAsync("/assets/images/lanyard.svg");
        Assert.Equal(HttpStatusCode.OK, lanyard.StatusCode);
        Assert.Contains("svg", lanyard.Content.Headers.ContentType?.MediaType);
        using var icon = await client.GetAsync("/assets/images/campfire-icon.png");
        Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        using var browserIcon = await client.GetAsync("/assets/images/browsers/chrome.svg");
        Assert.Equal(HttpStatusCode.OK, browserIcon.StatusCode);

        using var blocked = new HttpRequestMessage(HttpMethod.Get, "/");
        blocked.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 Firefox/114.0");
        using var blockedResponse = await client.SendAsync(blocked);
        var blockedHtml = await blockedResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotAcceptable, blockedResponse.StatusCode);
        AssertChrome(blockedHtml);
        Assert.Contains("class=\"panel", blockedHtml);
        Assert.Contains("Upgrade to a supported web browser", blockedHtml);
        Assert.Contains("Safari", blockedHtml);
        Assert.Contains("17.2+", blockedHtml);
        Assert.Contains("/assets/images/browsers/safari.svg", blockedHtml);
        Assert.Contains("/assets/images/globe.svg", blockedHtml);
        Assert.Contains("Upgrade to a supported web browser. Campfire requires a modern web browser.", blockedHtml);
        Assert.DoesNotContain("Georgia", blockedHtml);
        Assert.DoesNotContain("--ember", blockedHtml);

        var cookies = await HttpFlow.FirstRunAsync(client);

        using var signIn = await client.GetAsync("/session/new");
        var signInHtml = await signIn.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        AssertChrome(signInHtml);
        Assert.Contains("class=\"panel", signInHtml);
        Assert.Contains(">Campfire<", signInHtml);
        Assert.Contains("placeholder=\"Enter your email address\"", signInHtml);
        Assert.Contains("placeholder=\"Enter your password\"", signInHtml);
        Assert.Contains("name=\"csrf-token\"", signInHtml);
        Assert.Contains("name=\"authenticity_token\"", signInHtml);
        Assert.Contains("/assets/images/globe.svg", signInHtml);
        Assert.DoesNotContain("Georgia", signInHtml);

        using var home = new HttpRequestMessage(HttpMethod.Get, "/");
        HttpFlow.Add(home, cookies);
        using var redirected = await client.SendAsync(home);
        var roomPath = redirected.Headers.Location!.OriginalString;
        using var created = new HttpRequestMessage(HttpMethod.Post, roomPath + "/messages")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["body"] = "styled hello" }),
        };
        HttpFlow.Add(created, cookies);
        using var createdResponse = await client.SendAsync(created);
        Assert.Equal(HttpStatusCode.Redirect, createdResponse.StatusCode);

        using var room = new HttpRequestMessage(HttpMethod.Get, roomPath);
        HttpFlow.Add(room, cookies);
        using var roomResponse = await client.SendAsync(room);
        var roomHtml = await roomResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, roomResponse.StatusCode);
        AssertChrome(roomHtml);
        Assert.Contains("class=\"sidebar", roomHtml);
        Assert.Contains("room--current", roomHtml);
        Assert.Contains("id=\"message-area\"", roomHtml);
        Assert.Contains("class=\"message-area", roomHtml);
        Assert.Contains("class=\"messages\"", roomHtml);
        Assert.Contains("message__avatar", roomHtml);
        Assert.Contains("message__author", roomHtml);
        Assert.Contains("message__timestamp", roomHtml);
        Assert.Contains("message__body", roomHtml);
        Assert.Contains("styled hello", roomHtml);
        Assert.Contains("message--me", roomHtml);
        Assert.Contains("data-local-time-target=\"date\"", roomHtml);
        Assert.Contains("data-local-time-target=\"time\"", roomHtml);
        Assert.Contains("data-message-timestamp", roomHtml);
        Assert.Contains("/assets/images/globe.svg", roomHtml);
        Assert.DoesNotContain("October 5, 2026", roomHtml);
        Assert.DoesNotContain("5:04 AM", roomHtml);
        Assert.Contains("id=\"footer\"", roomHtml);
        Assert.Contains("id=\"composer\"", roomHtml);
        Assert.Contains("composer", roomHtml);
        Assert.Contains("room-live", roomHtml);
        Assert.True(roomHtml.IndexOf("id=\"footer\"", StringComparison.Ordinal) < roomHtml.IndexOf("id=\"composer\"", StringComparison.Ordinal));
        Assert.DoesNotContain("Georgia", roomHtml);
        Assert.DoesNotContain("--ember", roomHtml);
        var script = await client.GetStringAsync("/js/campfire.js");
        Assert.Contains("message__author", script);
        Assert.Contains("message__body", script);
        Assert.Contains("classList.add(\"unread\")", script);
        Assert.Contains("message--me", script);
        Assert.Contains("createdAt", script);
        Assert.Contains("/hubs/campfire", script);
        Assert.Contains("JoinRoom", script);

        var messageId = Regex.Match(roomHtml, "id=\"message-(\\d+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(messageId));
        using var boost = new HttpRequestMessage(HttpMethod.Post, $"{roomPath}/messages/{messageId}/boosts")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["content"] = "nice" }),
        };
        HttpFlow.Add(boost, cookies);
        using var boostResponse = await client.SendAsync(boost);
        Assert.Equal(HttpStatusCode.OK, boostResponse.StatusCode);
        using var boosted = new HttpRequestMessage(HttpMethod.Get, roomPath);
        HttpFlow.Add(boosted, cookies);
        using var boostedResponse = await client.SendAsync(boosted);
        var boostedHtml = await boostedResponse.Content.ReadAsStringAsync();
        Assert.Contains("boost boost-item", boostedHtml);
        Assert.Contains("nice", boostedHtml);
        Assert.Contains("message__author", boostedHtml);

        using var sidebar = new HttpRequestMessage(HttpMethod.Get, "/users/me/sidebar");
        HttpFlow.Add(sidebar, cookies);
        using var sidebarResponse = await client.SendAsync(sidebar);
        var sidebarHtml = await sidebarResponse.Content.ReadAsStringAsync();
        AssertChrome(sidebarHtml);
        Assert.Contains("id=\"sidebar\"", sidebarHtml);
        Assert.Contains("data-live", sidebarHtml);
        Assert.Contains("data-sidebar-room", sidebarHtml);
        Assert.Contains("class=\"align-center gap room btn", sidebarHtml);
        Assert.Contains("sidebar__container", sidebarHtml);
        Assert.DoesNotContain("Georgia", sidebarHtml);

        using var search = new HttpRequestMessage(HttpMethod.Get, "/searches?q=styled");
        HttpFlow.Add(search, cookies);
        using var searchResponse = await client.SendAsync(search);
        var searchHtml = await searchResponse.Content.ReadAsStringAsync();
        AssertChrome(searchHtml);
        Assert.Contains("class=\"sidebar searches", searchHtml);
        Assert.Contains("name=\"q\"", searchHtml);
        Assert.Contains("searches__input", searchHtml);
        Assert.Contains("styled hello", searchHtml);
        Assert.Contains("message__body", searchHtml);
        Assert.DoesNotContain("Georgia", searchHtml);

        using var account = new HttpRequestMessage(HttpMethod.Get, "/account");
        HttpFlow.Add(account, cookies);
        using var accountResponse = await client.SendAsync(account);
        var accountHtml = await accountResponse.Content.ReadAsStringAsync();
        AssertChrome(accountHtml);
        Assert.Contains("class=\"panel", accountHtml);
        Assert.Contains("id=\"join-code\"", accountHtml);
        Assert.Contains("name=\"custom_styles\"", accountHtml);
        Assert.Contains("Must be admin to create new rooms", accountHtml);
        Assert.Contains("Role: Administrator", accountHtml);
        Assert.Contains("My settings", accountHtml);
        Assert.Contains("/assets/images/crown.svg", accountHtml);
        Assert.Contains("/assets/images/pencil.svg", accountHtml);
        Assert.Contains("class=\"btn avatar\"", accountHtml);
        Assert.Contains("class=\"separator\"", accountHtml);
        Assert.Contains("checked", accountHtml);
        Assert.Contains("disabled", accountHtml);
        Assert.DoesNotContain(">Administrator<", accountHtml);
        Assert.DoesNotContain("Georgia", accountHtml);
        var token = HttpFlow.Token(accountHtml);
        using var styles = new HttpRequestMessage(HttpMethod.Post, "/account")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["settings"] = "1",
                ["custom_styles"] = "body{--ui-parity:1}",
                ["authenticity_token"] = token,
            }),
        };
        HttpFlow.Add(styles, cookies);
        using var stylesResponse = await client.SendAsync(styles);
        Assert.Equal(HttpStatusCode.Redirect, stylesResponse.StatusCode);
        using var styled = new HttpRequestMessage(HttpMethod.Get, "/account");
        HttpFlow.Add(styled, cookies);
        using var styledResponse = await client.SendAsync(styled);
        var styledHtml = await styledResponse.Content.ReadAsStringAsync();
        Assert.Contains("body{--ui-parity:1}", styledHtml);
        Assert.DoesNotContain("Georgia", styledHtml);
        Assert.DoesNotContain("--ember", styledHtml);

        var code = Regex.Match(accountHtml, "id=\"join-code\">([^<]+)<").Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(code));
        using var join = await client.GetAsync("/join/" + code);
        var joinHtml = await join.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        AssertChrome(joinHtml);
        Assert.Contains("class=\"signup", joinHtml);
        Assert.Contains("class=\"nametag", joinHtml);
        Assert.Contains("nametag__lanyard", joinHtml);
        Assert.Contains(">Campfire<", joinHtml);
        Assert.Contains("name=\"name\"", joinHtml);
        Assert.Contains("name=\"email_address\"", joinHtml);
        Assert.Contains("name=\"password\"", joinHtml);
        Assert.Contains("/assets/images/globe.svg", joinHtml);
        Assert.DoesNotContain("Georgia", joinHtml);

        using var joinerClient = HttpFlow.Client(factory);
        var joiner = new Dictionary<string, string>();
        using var joinPostPage = await joinerClient.GetAsync("/join/" + code);
        HttpFlow.Collect(joinPostPage, joiner);
        using var joinPost = new HttpRequestMessage(HttpMethod.Post, "/join/" + code)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["name"] = "Jason",
                ["email_address"] = "jason@37signals.com",
                ["password"] = "secret123456",
                ["authenticity_token"] = HttpFlow.Token(await joinPostPage.Content.ReadAsStringAsync()),
            }),
        };
        HttpFlow.Add(joinPost, joiner);
        using var joined = await joinerClient.SendAsync(joinPost);
        Assert.Equal(HttpStatusCode.Redirect, joined.StatusCode);
        using var members = new HttpRequestMessage(HttpMethod.Get, "/account");
        HttpFlow.Add(members, cookies);
        using var membersResponse = await client.SendAsync(members);
        var membersHtml = await membersResponse.Content.ReadAsStringAsync();
        Assert.Contains("/assets/images/minus.svg", membersHtml);
        Assert.Contains("Delete Jason", membersHtml);
        Assert.Contains("My settings", membersHtml);
        Assert.Contains("Role: Member", membersHtml);
        Assert.Contains("class=\"btn avatar\"", membersHtml);
        Assert.Contains("btn--negative", membersHtml);
        var menuStart = membersHtml.IndexOf("<menu", StringComparison.Ordinal);
        var menuEnd = membersHtml.IndexOf("</menu>", menuStart, StringComparison.Ordinal);
        var menu = membersHtml[menuStart..menuEnd];
        Assert.True(menu.IndexOf(">David<", StringComparison.Ordinal) < menu.IndexOf("separator full-width", StringComparison.Ordinal));
        Assert.True(menu.IndexOf("separator full-width", StringComparison.Ordinal) < menu.IndexOf(">Jason<", StringComparison.Ordinal));

        using var profile = new HttpRequestMessage(HttpMethod.Get, "/users/me/profile");
        HttpFlow.Add(profile, cookies);
        using var profileResponse = await client.SendAsync(profile);
        var profileHtml = await profileResponse.Content.ReadAsStringAsync();
        AssertChrome(profileHtml);
        Assert.Contains("class=\"panel", profileHtml);
        Assert.Contains("placeholder=\"Enter your name\"", profileHtml);
        Assert.Contains("placeholder=\"Enter your email address\"", profileHtml);
        Assert.Contains("placeholder=\"Change password\"", profileHtml);
        Assert.Contains("name=\"avatar\"", profileHtml);
        Assert.Contains("/avatar", profileHtml);
        Assert.Contains("/session/logout", profileHtml);
        Assert.Contains("membership-item", profileHtml);
        Assert.Contains("notification-bell-mentions.svg", profileHtml);
        Assert.Contains("Notifying about @ mentions", profileHtml);
        Assert.Contains("/assets/images/globe.svg", profileHtml);
        Assert.DoesNotContain("Georgia", profileHtml);

        using var user = new HttpRequestMessage(HttpMethod.Get, "/users/1");
        HttpFlow.Add(user, cookies);
        using var userResponse = await client.SendAsync(user);
        var userHtml = await userResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, userResponse.StatusCode);
        AssertChrome(userHtml);
        Assert.Contains("class=\"panel", userHtml);
        Assert.Contains("David", userHtml);
        Assert.Contains("Profile avatar", userHtml);
        Assert.Contains("btn btn--reversed full-width txt-large", userHtml);
        Assert.Contains("/assets/images/messages.svg", userHtml);
        Assert.Contains("aria-label=\"Ping David\"", userHtml);
        Assert.DoesNotContain("Georgia", userHtml);
    }

    private static void AssertChrome(string html)
    {
        Assert.Contains("skip-navigation", html);
        Assert.Contains("Skip to main content", html);
        Assert.Contains("id=\"nav\"", html);
        Assert.Contains("class=\"flash\"", html);
        Assert.Contains("id=\"main-content\"", html);
        Assert.Contains("id=\"footer\"", html);
        Assert.Contains("id=\"sidebar\"", html);
        Assert.Contains("class=\"lightbox\"", html);
        Assert.Contains("id=\"app-logo\"", html);
        Assert.Contains("name=\"viewport\" content=\"width=device-width, initial-scale=1, user-scalable=no, interactive-widget=resizes-content\"", html);
        Assert.Contains("name=\"color-scheme\" content=\"light dark\"", html);
        Assert.Contains("name=\"theme-color\" content=\"#ffffff\"", html);
        Assert.Contains("name=\"theme-color\" content=\"#000000\"", html);
        Assert.Contains("href=\"/assets/application.css\"", html);
        Assert.DoesNotContain("Georgia", html);
        Assert.DoesNotContain("--ember", html);
    }
}
