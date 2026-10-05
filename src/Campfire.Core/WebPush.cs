using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Campfire.Core;

public static class WebPush
{
    public static (string PublicKey, string PrivateKey) GenerateVapid()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(true);
        return (Base64Url.Encode(Uncompressed(parameters)), Base64Url.Encode(parameters.D!));
    }

    public static byte[] Encrypt(byte[] plaintext, string p256dh, string auth, out byte[] salt, out byte[] localPublic)
    {
        var clientPublic = Base64Url.Decode(p256dh);
        var authSecret = Base64Url.Decode(auth);
        if (clientPublic.Length != 65 || clientPublic[0] != 4)
            throw new AppException(422, "p256dh key is not an uncompressed P-256 point");
        if (authSecret.Length != 16)
            throw new AppException(422, "auth secret must be 16 bytes");

        using var local = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var localParameters = local.ExportParameters(false);
        localPublic = Uncompressed(localParameters);
        using var remote = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = clientPublic[1..33], Y = clientPublic[33..65] },
        });
        var shared = local.DeriveRawSecretAgreement(remote.PublicKey);
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), clientPublic, localPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, authSecret, keyInfo);

        salt = RandomNumberGenerator.GetBytes(16);
        var content = new byte[plaintext.Length + 1];
        plaintext.CopyTo(content, 0);
        content[^1] = 0x02;
        var recordSize = content.Length + 16;
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        var cipher = new byte[content.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(cek, 16);
        aes.Encrypt(nonce, content, cipher, tag);

        var body = new byte[16 + 4 + 1 + localPublic.Length + cipher.Length + tag.Length];
        salt.CopyTo(body, 0);
        body[16] = (byte)(recordSize >> 24);
        body[17] = (byte)(recordSize >> 16);
        body[18] = (byte)(recordSize >> 8);
        body[19] = (byte)recordSize;
        body[20] = (byte)localPublic.Length;
        localPublic.CopyTo(body, 21);
        cipher.CopyTo(body, 21 + localPublic.Length);
        tag.CopyTo(body, 21 + localPublic.Length + cipher.Length);
        return body;
    }

    public static byte[] Decrypt(byte[] body, byte[] clientPrivate, byte[] clientPublic, byte[] authSecret)
    {
        var salt = body[..16];
        var recordSize = (body[16] << 24) | (body[17] << 16) | (body[18] << 8) | body[19];
        var idLength = body[20];
        var localPublic = body[21..(21 + idLength)];
        var encrypted = body[(21 + idLength)..];
        using var client = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = clientPrivate,
            Q = new ECPoint { X = clientPublic[1..33], Y = clientPublic[33..65] },
        });
        using var remote = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = localPublic[1..33], Y = localPublic[33..65] },
        });
        var shared = client.DeriveRawSecretAgreement(remote.PublicKey);
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), clientPublic, localPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, authSecret, keyInfo);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        var cipherLength = encrypted.Length - 16;
        var plain = new byte[cipherLength];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, encrypted[..cipherLength], encrypted[cipherLength..], plain);
        if (plain[^1] != 0x02)
            throw new CryptographicException("padding delimiter missing");
        if (plain.Length + 16 != recordSize)
            throw new CryptographicException("record size does not match");
        return plain[..^1];
    }

    public static string Sign(string audience, string subject, DateTimeOffset expires, string publicKey, string privateKey)
    {
        var publicBytes = Base64Url.Decode(publicKey);
        var privateBytes = Base64Url.Decode(privateKey);
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateBytes,
            Q = new ECPoint { X = publicBytes[1..33], Y = publicBytes[33..65] },
        });
        var header = Base64Url.Encode(Encoding.ASCII.GetBytes("""{"typ":"JWT","alg":"ES256"}"""));
        var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["aud"] = audience,
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["sub"] = subject,
        });
        var payload = Base64Url.Encode(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = Encoding.ASCII.GetBytes(header + "." + payload);
        var signature = key.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return header + "." + payload + "." + Base64Url.Encode(signature);
    }

    public static string Payload(string title, string body, string path, int badge) =>
        JsonSerializer.Serialize(new
        {
            title,
            options = new
            {
                body,
                icon = "/account/logo",
                data = new { path, badge },
            },
        });

    private static byte[] Uncompressed(ECParameters parameters)
    {
        var point = new byte[65];
        point[0] = 4;
        Pad(parameters.Q.X!).CopyTo(point, 1);
        Pad(parameters.Q.Y!).CopyTo(point, 33);
        return point;
    }

    private static byte[] Pad(byte[] coordinate)
    {
        if (coordinate.Length == 32)
            return coordinate;
        var padded = new byte[32];
        coordinate.AsSpan(Math.Max(0, coordinate.Length - 32)).CopyTo(padded.AsSpan(32 - Math.Min(32, coordinate.Length)));
        return padded;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
