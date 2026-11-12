using System.Net;
using System.Net.Sockets;

namespace MMCA.Common.Infrastructure.Http;

/// <summary>
/// The address and target rules an outbound call to an operator-supplied URL is held to, so a stored
/// target cannot reach the host's own network (server-side request forgery). Used by outbound webhooks
/// at validation time and again at connect time.
/// </summary>
public static class OutboundAddressPolicy
{
    /// <summary>
    /// The denied IPv4 ranges, as (first address, prefix length). The single address
    /// <c>168.63.129.16</c> is the cloud platform's host endpoint, reachable from every VM although it
    /// sits in public address space.
    /// </summary>
    private static readonly (byte[] Network, int PrefixLength)[] DeniedIPv4 =
    [
        ([0, 0, 0, 0], 8),
        ([10, 0, 0, 0], 8),
        ([100, 64, 0, 0], 10),
        ([127, 0, 0, 0], 8),
        ([169, 254, 0, 0], 16),
        ([172, 16, 0, 0], 12),
        ([192, 0, 0, 0], 24),
        ([192, 168, 0, 0], 16),
        ([198, 18, 0, 0], 15),
        ([224, 0, 0, 0], 4),
        ([240, 0, 0, 0], 4),
        ([168, 63, 129, 16], 32),
    ];

    /// <summary>
    /// The only IPv6 space a target may be in: global unicast, <c>2000::/3</c>. IPv6 is an allow-list
    /// rather than a deny-list because several special formats outside it carry an IPv4 address the
    /// host's stack may still dial (IPv4-compatible <c>::/96</c>, both NAT64 prefixes), and a range
    /// added to the address architecture later is refused without anyone listing it.
    /// </summary>
    private static readonly (byte[] Network, int PrefixLength) GlobalUnicastIPv6 =
        ([0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 3);

    /// <summary>
    /// The ranges inside <see cref="GlobalUnicastIPv6"/> that are still denied, as (first address, prefix
    /// length): Teredo <c>2001::/32</c> and 6to4 <c>2002::/16</c>, which embed an IPv4 address a tunnel
    /// interface would reach, and the documentation prefixes <c>2001:db8::/32</c> and <c>3fff::/20</c>.
    /// </summary>
    private static readonly (byte[] Network, int PrefixLength)[] DeniedGlobalIPv6 =
    [
        ([0x20, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 32),
        ([0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 32),
        ([0x20, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 16),
        ([0x3F, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 20),
    ];

    /// <summary>
    /// Whether <paramref name="address"/> is a public address. False for IPv4 <c>0.0.0.0/8</c>,
    /// <c>10/8</c>, <c>100.64/10</c>, <c>127/8</c>, <c>169.254/16</c>, <c>172.16/12</c>,
    /// <c>192.0.0.0/24</c>, <c>192.168/16</c>, <c>198.18/15</c>, <c>224/4</c>, <c>240/4</c> and the single
    /// address <c>168.63.129.16</c>; for every IPv6 address outside <c>2000::/3</c> (which covers
    /// <c>::</c>, <c>::1</c>, IPv4-compatible <c>::/96</c>, both NAT64 prefixes, <c>fc00::/7</c>,
    /// <c>fe80::/10</c>, <c>fec0::/10</c> and <c>ff00::/8</c>) and inside it for <c>2001::/32</c>,
    /// <c>2001:db8::/32</c>, <c>2002::/16</c> and <c>3fff::/20</c>; and for any other address family. An IPv4-mapped IPv6
    /// address is judged as its IPv4 form.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns><see langword="true"/> when the address is public.</returns>
    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !IsInAny(address.GetAddressBytes(), DeniedIPv4);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return IsInRange(bytes, GlobalUnicastIPv6.Network, GlobalUnicastIPv6.PrefixLength)
            && !IsInAny(bytes, DeniedGlobalIPv6);
    }

    /// <summary>
    /// Whether <paramref name="target"/> may be called. Always: an absolute URI with scheme https or http,
    /// no user info and a non-empty host. Unless <paramref name="allowInsecure"/>: also https, a host that
    /// is not <c>localhost</c> or <c>*.localhost</c>, and, for an IP-literal host, a public address.
    /// </summary>
    /// <param name="target">The target URI.</param>
    /// <param name="allowInsecure">Whether http, local and private targets are allowed (Development only).</param>
    /// <returns><see langword="true"/> when the target is acceptable.</returns>
    public static bool IsAcceptableTargetUri(Uri target, bool allowInsecure)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!HasCallableShape(target))
        {
            return false;
        }

        return allowInsecure
            || IsScheme(target, Uri.UriSchemeHttps) && !IsLocalHostName(target.DnsSafeHost) && IsPublicIfLiteral(target);
    }

    /// <summary>Absolute, https or http, no user info, and a host.</summary>
    private static bool HasCallableShape(Uri target) =>
        target.IsAbsoluteUri
        && (IsScheme(target, Uri.UriSchemeHttps) || IsScheme(target, Uri.UriSchemeHttp))
        && target.UserInfo.Length == 0
        && !string.IsNullOrEmpty(target.DnsSafeHost);

    private static bool IsScheme(Uri target, string scheme) =>
        string.Equals(target.Scheme, scheme, StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalHostName(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>A host name passes (its addresses are checked when it is resolved); an IP literal must be public.</summary>
    private static bool IsPublicIfLiteral(Uri target) =>
        target.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6)
        || IPAddress.TryParse(target.DnsSafeHost, out var literal) && IsPublicAddress(literal);

    private static bool IsInAny(byte[] address, (byte[] Network, int PrefixLength)[] ranges) =>
        Array.Exists(ranges, range => IsInRange(address, range.Network, range.PrefixLength));

    private static bool IsInRange(byte[] address, byte[] network, int prefixLength)
    {
        var fullBytes = prefixLength / 8;
        if (!address.AsSpan(0, fullBytes).SequenceEqual(network.AsSpan(0, fullBytes)))
        {
            return false;
        }

        var remainingBits = prefixLength % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (address[fullBytes] & mask) == (network[fullBytes] & mask);
    }
}
