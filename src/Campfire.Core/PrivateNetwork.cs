using System.Net;
using System.Net.Sockets;

namespace Campfire.Core;

public static class PrivateNetwork
{
    public static bool IsPrivate(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return true;

        var text = address.Trim();
        if (!IPAddress.TryParse(text, out var ip))
            return true;

        // .NET can collapse mapped and compatible forms to IPv4. DNS never returns those forms.
        if (text.Contains(':') && ip.AddressFamily == AddressFamily.InterNetwork)
            return true;

        return IsPrivate(ip);
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return IsPrivateV4(ip.GetAddressBytes());

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 16)
            return true;

        if (IsSiit(bytes) || IsV4Compatible(bytes) || IsLocalNat64(bytes))
            return true;
        if (IsWellKnownNat64(bytes))
            return IsPrivateV4(bytes.AsSpan(12, 4));
        if (Is6to4(bytes))
            return IsPrivateV4(bytes.AsSpan(2, 4));
        if (IsTeredo(bytes) || IsDocumentation(bytes) || IsBenchmarking(bytes) || IsUla(bytes) || IsLinkLocalV6(bytes) || IsMulticast(bytes))
            return true;
        return false;
    }

    public static IPAddress Resolve(string host, Func<string, IReadOnlyList<IPAddress>> dns)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            if (IsPrivate(host))
                throw new ViolationException();
            return literal;
        }

        var addresses = dns(host);
        if (addresses.Count == 0)
            throw new UnresolvableException();

        IPAddress? firstPublic = null;
        foreach (var address in addresses)
        {
            if (!IsPrivate(address))
                firstPublic ??= address;
        }

        if (firstPublic is null)
            throw new ViolationException();
        return firstPublic;
    }

    private static bool IsPrivateV4(ReadOnlySpan<byte> b)
    {
        if (b.Length != 4)
            return true;
        if (b[0] is 0 or 10 or 127)
            return true;
        if (b[0] == 169 && b[1] == 254)
            return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            return true;
        if (b[0] == 192 && b[1] == 168)
            return true;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            return true;
        return false;
    }

    private static bool IsSiit(byte[] b) =>
        AllZero(b, 0, 8) && b[8] == 0xff && b[9] == 0xff && b[10] == 0 && b[11] == 0;

    private static bool IsV4Compatible(byte[] b) => AllZero(b, 0, 12);

    private static bool IsLocalNat64(byte[] b) =>
        b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4] == 0x00 && b[5] == 0x01;

    private static bool IsWellKnownNat64(byte[] b) =>
        b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && AllZero(b, 4, 8);

    private static bool Is6to4(byte[] b) => b[0] == 0x20 && b[1] == 0x02;

    private static bool IsTeredo(byte[] b) => b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00;

    private static bool IsDocumentation(byte[] b) => b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8;

    private static bool IsBenchmarking(byte[] b) =>
        b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x02 && b[4] == 0x00 && b[5] == 0x00;

    private static bool IsUla(byte[] b) => (b[0] & 0xfe) == 0xfc;

    private static bool IsLinkLocalV6(byte[] b) => b[0] == 0xfe && (b[1] & 0xc0) == 0x80;

    private static bool IsMulticast(byte[] b) => b[0] == 0xff;

    private static bool AllZero(byte[] bytes, int start, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (bytes[start + i] != 0)
                return false;
        }
        return true;
    }
}
