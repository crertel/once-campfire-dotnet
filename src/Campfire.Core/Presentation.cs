using System.Net;
using System.Text;

namespace Campfire.Core;

public static class Presentation
{
    public static string Body(Message message)
    {
        var sound = Sound.FromPlain(message.PlainText);
        if (sound is not null)
            return sound.Html();

        var builder = new StringBuilder(message.Html);
        if (message.Attachment is { } attachment)
            builder.Append(AttachmentHtml(message.Id, attachment));
        return builder.ToString();
    }

    public static string AttachmentHtml(long messageId, Attachment attachment)
    {
        var href = "/messages/" + messageId + "/attachment";
        var name = WebUtility.HtmlEncode(attachment.FileName);
        if (attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            var width = attachment.Width ?? 0;
            var height = attachment.Height ?? 0;
            var style = width > 0 && height > 0
                ? $" style=\"width: {Math.Min(width, 600)}px; aspect-ratio: {width} / {height};\""
                : "";
            return $"<div class=\"max-inline-size center flex overflow-clip\"{style}><a href=\"{href}\" class=\"lightbox-link\"><img src=\"{href}\" class=\"message__attachment\" alt=\"{name}\" loading=\"lazy\"></a></div>";
        }

        if (attachment.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return $"<div class=\"max-inline-size center overflow-clip\"><video src=\"{href}\" class=\"message__attachment\" controls preload=\"none\"></video></div>";

        if (attachment.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return $"<audio src=\"{href}\" controls preload=\"none\"></audio>";

        return $"<div class=\"flex-inline align-center gap-half\"><img src=\"/assets/images/common-file-text.svg\" alt=\"\" width=\"22\" height=\"22\" class=\"colorize--black\"><span>{name}</span><a class=\"btn\" href=\"{href}?download=1\">Download</a></div>";
    }
}
