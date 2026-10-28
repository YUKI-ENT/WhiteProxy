using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace WhiteProxy.Core;

public static class HostRules
{
    public static string Normalize(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host != host.Trim() || host.Contains('%'))
            throw new ArgumentException("ホスト名が不正です。");
        host = host.TrimEnd('.');
        if (IPAddress.TryParse(host.Trim('[', ']'), out var address))
            return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        var ascii = new IdnMapping().GetAscii(host).ToLowerInvariant();
        if (ascii.Length > 253 || ascii.Split('.').Any(label => label.Length is < 1 or > 63 ||
            label[0] == '-' || label[^1] == '-' || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new ArgumentException("ホスト名が不正です。");
        return ascii;
    }

    public static bool Matches(string host, string pattern)
    {
        host = Normalize(host);
        if (!pattern.StartsWith("*.", StringComparison.Ordinal)) return host == Normalize(pattern);
        if (IPAddress.TryParse(host, out _)) return false;
        var suffix = Normalize(pattern[2..]);
        return host.Length > suffix.Length + 1 && host.EndsWith('.' + suffix, StringComparison.Ordinal);
    }
}

public readonly record struct Ipv4Network(uint Address, int Prefix)
{
    private uint Mask => Prefix == 0 ? 0 : uint.MaxValue << (32 - Prefix);
    public static Ipv4Network Parse(string value)
    {
        var parts = value.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            (parts.Length == 2 && (!int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32)))
            throw new ArgumentException("IPv4 または IPv4/CIDR を入力してください。");
        var result = new Ipv4Network(ToUInt(ip), parts.Length == 1 ? 32 : int.Parse(parts[1]));
        return result with { Address = result.Address & result.Mask };
    }
    public bool Contains(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.AddressFamily == AddressFamily.InterNetwork && (ToUInt(ip) & Mask) == Address;
    }
    public bool Overlaps(Ipv4Network other) => (Address & other.Mask) == other.Address || (other.Address & Mask) == Address;
    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}
