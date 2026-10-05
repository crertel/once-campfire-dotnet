using Campfire.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Campfire.Web.Pages;

public sealed class RoomCreateModel(CampfireApp app, CampfireDb db) : CampfirePage(app, db)
{
    public string Kind { get; private set; } = "opens";
    public string Name { get; private set; } = "New room";
    public IReadOnlyList<User> People { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(string kind)
    {
        if (SignedOut() is { } redirect)
            return redirect;
        if (!RoomKinds.Known(kind))
            return NotFound();
        if (!await CanCreateAsync())
            return Forbid();
        Kind = kind;
        People = await PeopleAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string kind)
    {
        if (SignedOut() is { } redirect)
            return redirect;
        if (!RoomKinds.Known(kind))
            return NotFound();
        Kind = kind;
        var form = await Request.ReadFormAsync();
        try
        {
            var ids = Ids(form);
            Room room;
            if (kind == "directs")
                room = await App.FindOrCreateDirectAsync(CurrentUser!.Id, ids);
            else
            {
                var name = form["room[name]"].ToString();
                Name = name;
                room = await App.CreateRoomAsync(CurrentUser!.Id, name, kind == "opens" ? RoomKind.Open : RoomKind.Closed, ids);
            }
            return Redirect($"/rooms/{room.Id}");
        }
        catch (AppException exception)
        {
            Error = exception.Message;
            People = await PeopleAsync();
            return Page();
        }
    }

    private async Task<bool> CanCreateAsync()
    {
        var account = await App.AccountAsync();
        return CurrentUser!.IsAdministrator || !account.RestrictRoomCreationToAdministrators;
    }

    private async Task<List<User>> PeopleAsync() =>
        await Db.Users.Where(user => user.Status == UserStatus.Active && user.Role != UserRole.Bot).OrderBy(user => user.Name).ToListAsync();

    private static List<long> Ids(IFormCollection form) =>
        form["user_ids"].Select(value => long.TryParse(value, out var id) ? id : 0L).Where(id => id > 0).ToList();
}

public sealed class RoomEditModel(CampfireApp app, CampfireDb db) : CampfirePage(app, db)
{
    public string Kind { get; private set; } = "opens";
    public Room? Room { get; private set; }
    public IReadOnlyList<User> People { get; private set; } = [];
    public HashSet<long> Selected { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(string kind, long id)
    {
        if (SignedOut() is { } redirect)
            return redirect;
        if (await LoadAsync(kind, id) is { } result)
            return result;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string kind, long id)
    {
        if (SignedOut() is { } redirect)
            return redirect;
        if (await LoadAsync(kind, id) is { } result)
            return result;
        var form = await Request.ReadFormAsync();
        try
        {
            var target = kind == "opens" ? RoomKind.Open : RoomKind.Closed;
            await App.SaveRoomAsync(CurrentUser!.Id, id, target, form["room[name]"].ToString(), Ids(form));
            return Redirect($"/rooms/{id}");
        }
        catch (AppException exception)
        {
            Error = exception.Message;
            return Page();
        }
    }

    private async Task<IActionResult?> LoadAsync(string kind, long id)
    {
        if (!RoomKinds.Known(kind))
            return NotFound();
        Kind = kind;
        Room = await Db.Rooms.Include(room => room.Memberships).ThenInclude(membership => membership.User).FirstOrDefaultAsync(room => room.Id == id);
        if (Room is null || !await App.IsMemberAsync(CurrentUser!.Id, id))
            return NotFound();
        if (Room.Kind == RoomKind.Direct && kind != "directs")
            return NotFound();
        if (Room.Kind != RoomKind.Direct && kind == "directs")
            return NotFound();
        People = await Db.Users.Where(user => user.Status == UserStatus.Active && user.Role != UserRole.Bot).OrderBy(user => user.Name).ToListAsync();
        Selected = Room.Memberships.Select(membership => membership.UserId).ToHashSet();
        return null;
    }

    private static List<long> Ids(IFormCollection form) =>
        form["user_ids"].Select(value => long.TryParse(value, out var id) ? id : 0L).Where(id => id > 0).ToList();
}

public sealed class BotsModel(CampfireApp app, CampfireDb db) : CampfirePage(app, db)
{
    public IReadOnlyList<User> Bots { get; private set; } = [];
    public IReadOnlyList<Room> Rooms { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await GuardAsync() is { } redirect)
            return redirect;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await GuardAsync() is { } redirect)
            return redirect;
        var form = await Request.ReadFormAsync();
        try
        {
            if (form["create"] == "1")
                await App.CreateBotAsync(CurrentUser!.Id, form["name"].ToString(), form["webhook_url"].ToString());
            else if (long.TryParse(form["bot_id"], out var botId) && form["deactivate"] == "1")
                await App.DeactivateBotAsync(CurrentUser!.Id, botId);
            else if (long.TryParse(form["bot_id"], out botId) && form["reset"] == "1")
                await App.ResetBotKeyAsync(CurrentUser!.Id, botId);
            else if (long.TryParse(form["bot_id"], out botId))
                await App.UpdateBotAsync(CurrentUser!.Id, botId, form["name"].ToString(), form["webhook_url"].ToString());
        }
        catch (AppException exception)
        {
            Error = exception.Message;
        }
        await LoadAsync();
        return Page();
    }

    private async Task<IActionResult?> GuardAsync()
    {
        if (SignedOut() is { } redirect)
            return redirect;
        return CurrentUser!.IsAdministrator ? null : Forbid();
    }

    private async Task LoadAsync()
    {
        Bots = await App.BotsAsync();
        Rooms = await Db.Rooms.Where(room => room.Kind != RoomKind.Direct).OrderBy(room => room.Name).ToListAsync();
    }
}

public sealed class TransferModel(CampfireApp app, CampfireDb db) : CampfirePage(app, db)
{
    public string? Error { get; private set; }

    public IActionResult OnGet() => Page();

    public async Task<IActionResult> OnPostAsync(string id)
    {
        var user = await App.RedeemTransferAsync(id);
        if (user is null)
        {
            Error = "That sign-in link has expired.";
            return Page();
        }
        var session = await App.StartSessionAsync(user, HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString());
        Response.Cookies.Append(SessionCookies.Name, session.Token, SessionCookies.Append);
        return Redirect("/");
    }
}

public static class RoomKinds
{
    public static bool Known(string kind) => kind is "opens" or "closeds" or "directs";
}
