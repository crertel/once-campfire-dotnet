namespace Campfire.Core;

public static class ImageSniff
{
    public static string? ContentType(ReadOnlySpan<byte> bytes)
    {
        if (IsPng(bytes))
            return "image/png";
        if (IsJpeg(bytes))
            return "image/jpeg";
        if (IsGif(bytes))
            return "image/gif";
        if (IsWebp(bytes))
            return "image/webp";
        return null;
    }

    public static (int Width, int Height)? Size(ReadOnlySpan<byte> bytes)
    {
        if (IsPng(bytes) && bytes.Length >= 24)
            return (ReadBe(bytes[16..20]), ReadBe(bytes[20..24]));
        if (IsGif(bytes) && bytes.Length >= 10)
            return (bytes[6] | (bytes[7] << 8), bytes[8] | (bytes[9] << 8));
        if (IsJpeg(bytes))
            return JpegSize(bytes);
        if (IsWebp(bytes))
            return WebpSize(bytes);
        return null;
    }

    private static int ReadBe(ReadOnlySpan<byte> bytes) =>
        (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];

    private static (int Width, int Height)? JpegSize(ReadOnlySpan<byte> bytes)
    {
        var index = 2;
        while (index + 8 < bytes.Length)
        {
            if (bytes[index] != 0xFF)
                return null;
            var marker = bytes[index + 1];
            var length = (bytes[index + 2] << 8) | bytes[index + 3];
            if (marker is 0xC0 or 0xC1 or 0xC2 && index + 8 < bytes.Length)
                return ((bytes[index + 7] << 8) | bytes[index + 8], (bytes[index + 5] << 8) | bytes[index + 6]);
            if (length < 2)
                return null;
            index += 2 + length;
        }
        return null;
    }

    private static (int Width, int Height)? WebpSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 30 && bytes[12] == (byte)'V' && bytes[13] == (byte)'P' && bytes[14] == (byte)'8' && bytes[15] == (byte)' ')
        {
            var width = (bytes[26] | (bytes[27] << 8) | (bytes[28] << 16)) & 0x3FFF;
            var height = ((bytes[28] >> 6) | (bytes[29] << 2) | ((bytes.Length > 30 ? bytes[30] : 0) << 10)) & 0x3FFF;
            return width > 0 && height > 0 ? (width, height) : null;
        }
        return null;
    }

    private static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

    private static bool IsJpeg(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private static bool IsGif(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') && bytes[5] == (byte)'a';

    private static bool IsWebp(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';
}
