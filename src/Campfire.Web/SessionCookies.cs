namespace Campfire.Web;

public static class SessionCookies
{
    public const string Name = "session_token";

    public static CookieOptions Append { get; } = new()
    {
        HttpOnly = true,
        IsEssential = true,
        MaxAge = TimeSpan.FromDays(3650),
        Path = "/",
        SameSite = SameSiteMode.Lax,
        Secure = false,
    };

    public static CookieOptions Delete { get; } = new()
    {
        Path = "/",
        SameSite = SameSiteMode.Lax,
        Secure = false,
    };
}
