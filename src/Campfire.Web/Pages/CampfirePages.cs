using Campfire.Core;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Campfire.Web.Pages;

public abstract class CampfirePage(CampfireApp app, CampfireDb db) : PageModel
{
    protected CampfireApp App { get; } = app;
    protected CampfireDb Db { get; } = db;
    public User? CurrentUser => HttpContext.Items["User"] as User;

    protected IActionResult? SignedOut() => CurrentUser is null ? Redirect("/session/new") : null;
}

public sealed class IndexModel(CampfireApp app, CampfireDb db, IAntiforgery antiforgery) : CampfirePage(app, db)
{
    public string Csrf { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await App.HasAccountAsync())
        {
            Csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "";
            return Page();
        }
        if (CurrentUser is null)
            return Redirect("/session/new");
        var room = await App.OriginalRoomAsync(CurrentUser.Id);
        return room is null ? Redirect("/account") : Redirect($"/rooms/{room.Id}");
    }
}

public sealed class FirstRunModel(CampfireApp app, CampfireDb db, IAntiforgery antiforgery) : CampfirePage(app, db)
{
    public string? Error { get; private set; }
    public string Csrf { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync()
    {
        if (await App.HasAccountAsync())
            return Redirect("/");
        Csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var form = await Request.ReadFormAsync();
        try
        {
            var user = await App.FirstRunAsync(Field(form, "user[name]", "name"), Field(form, "user[email_address]", "email_address"), Field(form, "user[password]", "password"));
            var session = await App.LoginAsync(user.EmailAddress!, form["user[password]"].ToString().Length > 0 ? form["user[password]"].ToString() : form["password"].ToString(), HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString());
            if (session is not null)
                Response.Cookies.Append(SessionCookies.Name, session.Token, SessionCookies.Append);
            return Redirect("/");
        }
        catch (AppException exception)
        {
            Error = exception.Message;
            Csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "";
            return Page();
        }
    }

    private static string Field(IFormCollection form, string primary, string fallback)
    {
        var value = form[primary].ToString();
        return string.IsNullOrEmpty(value) ? form[fallback].ToString() : value;
    }
}

public sealed class SessionNewModel(CampfireApp app, CampfireDb db, IAntiforgery antiforgery) : CampfirePage(app, db)
{
    public string Csrf { get; private set; } = "";
    public Account? Account { get; private set; }
    public User? Owner { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await App.HasAccountAsync())
            return Redirect("/first_run");
        Csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "";
        Account = await App.AccountAsync();
        Owner = await Db.Users.Where(user => user.Role == UserRole.Administrator).OrderBy(user => user.Id).FirstOrDefaultAsync();
        return Page();
    }
}

public sealed class RoomModel(CampfireApp app, CampfireDb db) : CampfirePage(app, db)
{
    public Room? Room { get; private set; }
    public string Title { get; private set; } = "";
    public IReadOnlyList<Message> Messages { get; private set; } = [];
    public IReadOnlyList<Membership> Memberships { get; private set; } = [];
    public string Draft { get; private set; } = "";
    public long RoomId { get; private set; }
    public bool ShowWelcome { get; private set; }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        if (SignedOut() is { } redirect)
            return redirect;
        return await LoadAsync(id) ?? Page();
    }

    public async Task<IActionResult> OnPostAsync(long id)
    {
        if (SignedOut() is { } redirect)
            return redirect;
        var form = await Request.ReadFormAsync();
        if (form["draft"] == "1")
            await App.SaveDraftAsync(CurrentUser!.Id, id, form["body"].ToString());
        else
            await App.CreateMessageAsync(CurrentUser!.Id, id, form["body"].ToString(), NullIfEmpty(form["client_message_id"].ToString()));
        return Redirect($"/rooms/{id}");
    }

    private async Task<IActionResult?> LoadAsync(long id)
    {
        if (!await App.IsMemberAsync(CurrentUser!.Id, id))
            return NotFound();
        RoomId = id;
        Room = await Db.Rooms.Include(room => room.Memberships).ThenInclude(membership => membership.User).FirstAsync(room => room.Id == id);
        Title = Room.Kind == RoomKind.Direct
            ? string.Join(", ", Room.Memberships.Where(membership => membership.UserId != CurrentUser.Id).Select(membership => membership.User.Name))
            : Room.Name ?? "Room";
        if (Title.Length == 0)
            Title = "Direct";
        var page = await App.MessagesPageAsync(id, null, null);
        Messages = page.Messages;
        Memberships = await App.SidebarAsync(CurrentUser.Id);
        var original = await App.OriginalRoomAsync(CurrentUser.Id);
        ShowWelcome = original?.Id == id && Messages.Count < 40;
        Draft = await App.DraftAsync(CurrentUser.Id, id) ?? "";
        return null;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}

public sealed class SidebarModel(CampfireApp app, CampfireDb db) : CampfirePage(app, db)
{
    public IReadOnlyList<Membership> Memberships { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (SignedOut() is { } redirect)
            return redirect;
        Memberships = await App.SidebarAsync(CurrentUser!.Id);
        return Page();
    }
}

public sealed class SearchModel(CampfireApp app, CampfireDb db) : CampfirePage(app, db)
{
    public string Query { get; private set; } = "";
    public IReadOnlyList<Message> Results { get; private set; } = [];
    public IReadOnlyList<SearchQuery> Recent { get; private set; } = [];
    public IReadOnlyList<Membership> Memberships { get; private set; } = [];
    public long? ReturnRoomId { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? q)
    {
        if (SignedOut() is { } redirect)
            return redirect;
        Query = q ?? "";
        if (Query.Length > 0)
            Results = await App.SearchAsync(CurrentUser!.Id, Query);
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (SignedOut() is { } redirect)
            return redirect;
        var rows = await Db.SearchQueries.Where(query => query.UserId == CurrentUser!.Id).ToListAsync();
        Db.SearchQueries.RemoveRange(rows);
        await Db.SaveChangesAsync();
        return Redirect("/searches");
    }

    private async Task LoadAsync()
    {
        Memberships = await App.SidebarAsync(CurrentUser!.Id);
        ReturnRoomId = Memberships.FirstOrDefault(membership => membership.Room.Kind != RoomKind.Direct)?.RoomId
            ?? Memberships.FirstOrDefault()?.RoomId;
        Recent = await Db.SearchQueries.Where(query => query.UserId == CurrentUser.Id)
            .OrderByDescending(query => query.CreatedAt)
            .Take(20)
            .ToListAsync();
        if (Results.Count == 0)
            return;
        var ids = Results.Select(message => message.Id).ToList();
        var roomIds = Results.Select(message => message.RoomId).Distinct().ToList();
        var boosts = await Db.Boosts.Include(boost => boost.Booster).Where(boost => ids.Contains(boost.MessageId)).ToListAsync();
        var rooms = await Db.Rooms.Where(room => roomIds.Contains(room.Id)).ToDictionaryAsync(room => room.Id);
        foreach (var message in Results)
        {
            message.Boosts = boosts.Where(boost => boost.MessageId == message.Id).ToList();
            if (rooms.TryGetValue(message.RoomId, out var room))
                message.Room = room;
        }
    }
}

public sealed class AccountModel(CampfireApp app, CampfireDb db) : CampfirePage(app, db)
{
    public Account? Account { get; private set; }
    public IReadOnlyList<User> Users { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (SignedOut() is { } redirect)
            return redirect;
        Account = await App.AccountAsync();
        Users = await App.UsersAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (SignedOut() is { } redirect)
            return redirect;
        var form = await Request.ReadFormAsync();
        if (form["settings"] == "1")
        {
            await App.SetCustomStylesAsync(CurrentUser!.Id, form["custom_styles"].ToString());
            await App.SetRoomCreationPolicyAsync(CurrentUser.Id, form["restrict"] == "1");
        }
        if (form["reset_join_code"] == "1")
            await App.ResetJoinCodeAsync(CurrentUser!.Id);
        return Redirect("/account");
    }
}

public sealed class JoinModel(CampfireApp app, CampfireDb db) : CampfirePage(app, db)
{
    public string Code { get; private set; } = "";
    public string? Error { get; private set; }
    public string AccountName { get; private set; } = "";
    public User? Owner { get; private set; }

    public async Task<IActionResult> OnGetAsync(string code)
    {
        Code = code;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string code)
    {
        Code = code;
        var form = await Request.ReadFormAsync();
        try
        {
            var user = await App.JoinAsync(code, form["name"].ToString(), form["email_address"].ToString(), form["password"].ToString());
            var session = await App.LoginAsync(user.EmailAddress!, form["password"].ToString(), HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString());
            if (session is not null)
                Response.Cookies.Append(SessionCookies.Name, session.Token, SessionCookies.Append);
            return Redirect("/");
        }
        catch (AppException exception)
        {
            Error = exception.Message;
            await LoadAsync();
            return Page();
        }
    }

    private async Task LoadAsync()
    {
        var account = await App.AccountAsync();
        AccountName = account.Name;
        Owner = await Db.Users.Where(user => user.Role == UserRole.Administrator).OrderBy(user => user.Id).FirstOrDefaultAsync();
    }
}

public sealed class ProfileModel(CampfireApp app, CampfireDb db) : CampfirePage(app, db)
{
    public IReadOnlyList<Membership> Memberships { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (SignedOut() is { } redirect)
            return redirect;
        Memberships = await App.SidebarAsync(CurrentUser!.Id);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (SignedOut() is { } redirect)
            return redirect;
        var form = await Request.ReadFormAsync();
        await App.UpdateProfileAsync(CurrentUser!.Id, form["name"].ToString(), form["email_address"].ToString(), form["bio"].ToString(), form["password"].ToString());
        return Redirect("/users/me/profile");
    }
}

public sealed class UserModel(CampfireApp app, CampfireDb db) : CampfirePage(app, db)
{
    public User? Shown { get; private set; }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        if (SignedOut() is { } redirect)
            return redirect;
        Shown = await Db.Users.FirstOrDefaultAsync(user => user.Id == id);
        return Shown is null ? NotFound() : Page();
    }
}
