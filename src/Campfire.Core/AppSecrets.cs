using System.Security.Cryptography;
using System.Text;

namespace Campfire.Core;

public sealed class AppSecrets
{
    public AppSecrets(byte[] transferKey, string? vapidPublicKey, string? vapidPrivateKey)
    {
        if (transferKey.Length < 32)
            throw new ArgumentException("Transfer key must be at least 32 bytes.", nameof(transferKey));
        TransferKey = transferKey;
        VapidPublicKey = vapidPublicKey;
        VapidPrivateKey = vapidPrivateKey;
    }

    public byte[] TransferKey { get; }

    public string? VapidPublicKey { get; }

    public string? VapidPrivateKey { get; }

    public static AppSecrets Load(string directory)
    {
        Directory.CreateDirectory(directory);
        var transferPath = Path.Combine(directory, "transfer.key");
        byte[] transfer;
        if (File.Exists(transferPath))
            transfer = File.ReadAllBytes(transferPath);
        else
        {
            transfer = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(transferPath, transfer);
        }

        var configuredPublic = Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY");
        var configuredPrivate = Environment.GetEnvironmentVariable("VAPID_PRIVATE_KEY");
        if (!string.IsNullOrWhiteSpace(configuredPublic) && !string.IsNullOrWhiteSpace(configuredPrivate))
            return new AppSecrets(transfer, configuredPublic.Trim(), configuredPrivate.Trim());

        var vapidPath = Path.Combine(directory, "vapid.json");
        if (File.Exists(vapidPath))
        {
            var saved = System.Text.Json.JsonSerializer.Deserialize<VapidFile>(File.ReadAllText(vapidPath));
            if (saved?.PublicKey is not null && saved.PrivateKey is not null)
                return new AppSecrets(transfer, saved.PublicKey, saved.PrivateKey);
        }

        var (publicKey, privateKey) = WebPush.GenerateVapid();
        File.WriteAllText(vapidPath, System.Text.Json.JsonSerializer.Serialize(new VapidFile(publicKey, privateKey)));
        return new AppSecrets(transfer, publicKey, privateKey);
    }

    private sealed record VapidFile(string PublicKey, string PrivateKey);
}

public static class Transfers
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);

    public static string Issue(long userId, DateTimeOffset expires, byte[] key)
    {
        var payload = $"{userId}.{expires.ToUnixTimeSeconds()}";
        var signature = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));
        return Base64Url.Encode(Encoding.UTF8.GetBytes(payload)) + "." + Base64Url.Encode(signature);
    }

    public static long? Read(string token, byte[] key, DateTimeOffset now)
    {
        var parts = token.Split('.');
        if (parts.Length != 2)
            return null;
        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = Base64Url.Decode(parts[0]);
            signature = Base64Url.Decode(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        var expected = HMACSHA256.HashData(key, payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
            return null;
        var payload = Encoding.UTF8.GetString(payloadBytes);
        var dot = payload.IndexOf('.');
        if (dot <= 0 || !long.TryParse(payload[..dot], out var userId) || !long.TryParse(payload[(dot + 1)..], out var expiry))
            return null;
        if (DateTimeOffset.FromUnixTimeSeconds(expiry) < now)
            return null;
        return userId;
    }
}

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
