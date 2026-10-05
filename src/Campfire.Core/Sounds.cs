using System.Net;
using System.Text.RegularExpressions;

namespace Campfire.Core;

public sealed partial record Sound(string Name, string? Text, string? Image, int Width, int Height)
{
    public static Sound? Find(string name) => Catalog.FirstOrDefault(sound => sound.Name == name);

    public static IReadOnlyList<Sound> All => Catalog;

    public static Sound? FromPlain(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain))
            return null;
        var match = Play().Match(plain.Trim());
        return match.Success ? Find(match.Groups["name"].Value) : null;
    }

    public string Html()
    {
        var source = "/assets/sounds/" + WebUtility.HtmlEncode(Name) + ".mp3";
        var caption = Image is null
            ? WebUtility.HtmlEncode(Text ?? Name)
            : $"<img src=\"/assets/images/sounds/{WebUtility.HtmlEncode(Image)}\" alt=\"\" width=\"{Width}\" height=\"{Height}\">";
        return $"<figure class=\"sound\"><audio class=\"sound__audio\" src=\"{source}\" controls preload=\"none\"></audio><figcaption>{caption}</figcaption></figure>";
    }

    [GeneratedRegex(@"\A/play (?<name>\w+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex Play();

    private static readonly Sound[] Catalog =
    [
        new("56k", null, "56k.webp", 79, 33),
        new("bell", "🔔", null, 0, 0),
        new("bezos", "😆💭", null, 0, 0),
        new("bueller", "anyone?", null, 0, 0),
        new("butts", "👐 🚬", null, 0, 0),
        new("clowntown", null, "clowntown.webp", 210, 150),
        new("cottoneyejoe", "🎶🙉🎶", null, 0, 0),
        new("crickets", "hears crickets chirping", null, 0, 0),
        new("curb", null, "curb.webp", 150, 101),
        new("dadgummit", "dad gummit!! 🎣", null, 0, 0),
        new("dangerzone", null, "dangerzone.webp", 157, 32),
        new("danielsan", "🎆 🏆 🎆", null, 0, 0),
        new("deeper", null, "top.webp", 188, 80),
        new("ballmer", "developers!", null, 0, 0),
        new("donotwant", null, "donotwant.webp", 150, 150),
        new("drama", null, "drama.webp", 300, 16),
        new("flawless", "#flawless", null, 0, 0),
        new("glados", "🤖💢", null, 0, 0),
        new("gogogo", "Go, go, go!", null, 0, 0),
        new("greatjob", null, "greatjob.webp", 79, 16),
        new("greyjoy", "😖🎺", null, 0, 0),
        new("guarantee", "guarantees it 👌", null, 0, 0),
        new("heygirl", "✨💁✨", null, 0, 0),
        new("honk", "HONK", null, 0, 0),
        new("horn", "🐶 ✂️ 🐱", null, 0, 0),
        new("horror", "💀 💀 💀 💀 💀 💀 💀", null, 0, 0),
        new("inconceivable", "doesn't think it means what you think it means…", null, 0, 0),
        new("letitgo", "❄️👩❄️⛄️❄️", null, 0, 0),
        new("live", "is DOING IT LIVE", null, 0, 0),
        new("loggins", null, "loggins.webp", 200, 151),
        new("makeitso", "make it so 👉", null, 0, 0),
        new("noooo", "👸💀😒", null, 0, 0),
        new("nyan", null, "nyan.webp", 36, 15),
        new("ohmy", "raises an eyebrow 😏", null, 0, 0),
        new("ohyeah", "isn't playing by the rules", null, 0, 0),
        new("pushit", null, "pushit.webp", 104, 15),
        new("rimshot", "plays a rimshot", null, 0, 0),
        new("rollout", "is rolling out 🚗", null, 0, 0),
        new("rumble", null, "rumble.webp", 220, 150),
        new("sax", "🌇🎷🎶", null, 0, 0),
        new("secret", "found a secret area 🔑", null, 0, 0),
        new("sexyback", "🔞", null, 0, 0),
        new("story", "and now you know…", null, 0, 0),
        new("tada", "plays a fanfare 🎏", null, 0, 0),
        new("tmyk", "✨ ⭐️ The More You Know ✨ ⭐️", null, 0, 0),
        new("totes", "😁👍", null, 0, 0),
        new("trololo", "трололо", null, 0, 0),
        new("trombone", "plays a sad trombone", null, 0, 0),
        new("unix", "knows this 💻", null, 0, 0),
        new("vuvuzela", "======<() ~ ♪ ~♫", null, 0, 0),
        new("what", null, "what.webp", 100, 131),
        new("whoomp", "👏‼️😎", null, 0, 0),
        new("wups", "wups!", null, 0, 0),
        new("yay", null, "yay.webp", 103, 50),
        new("yeah", null, "yeah.webp", 104, 15),
        new("yodel", "📣🗻🙉", null, 0, 0),
    ];
}
