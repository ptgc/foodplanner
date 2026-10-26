using System.Net;
using System.Net.Sockets;

namespace Foodplanner.Infrastructure.Import;

/// <summary>
/// Which addresses a server-side fetch may connect to: public unicast addresses only (SSRF guard). Everything that can
/// reach the host itself, the container network, the cloud metadata service or a neighbour is refused:
/// IPv4 0/8, 10/8, 100.64/10 (CGNAT), 127/8, 169.254/16 (link-local, 169.254.169.254 metadata), 172.16/12, 192.0.0/24,
/// 192.0.2/24, 192.88.99/24, 192.168/16, 198.18/15, 198.51.100/24, 203.0.113/24, 224/4 (multicast) and 240/4 (reserved,
/// 255.255.255.255). IPv6: anything outside global unicast 2000::/3 (so ::, ::1, fc00::/7, fe80::/10, fec0::/10, ff00::/8, ...),
/// Teredo and IETF-reserved 2001::/23, documentation ranges, and the forms that carry an IPv4 address (IPv4-mapped
/// ::ffff:a.b.c.d, NAT64 64:ff9b::/96, 6to4 2002::/16) which are judged by the IPv4 address they embed.
/// </summary>
public static class IpAddressRules
{
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicV4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicV6(address.GetAddressBytes()),
            _ => false,
        };
    }

    private static bool IsPublicV4(byte[] b)
    {
        var (a, second, third) = (b[0], b[1], b[2]);
        return !(a == 0
            || a == 10
            || (a == 100 && (second & 0xC0) == 64)
            || a == 127
            || (a == 169 && second == 254)
            || (a == 172 && (second & 0xF0) == 16)
            || (a == 192 && second == 0 && third is 0 or 2)
            || (a == 192 && second == 88 && third == 99)
            || (a == 192 && second == 168)
            || (a == 198 && (second & 0xFE) == 18)
            || (a == 198 && second == 51 && third == 100)
            || (a == 203 && second == 0 && third == 113)
            || a >= 224);
    }

    private static bool IsPublicV6(byte[] b)
    {
        // NAT64 well-known prefix 64:ff9b::/96 carries an IPv4 address in the last 32 bits.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4..12].All(x => x == 0))
        {
            return IsPublicV4(b[12..16]);
        }

        // 6to4 2002::/16 carries the IPv4 address in bits 16 to 47.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsPublicV4(b[2..6]);
        }

        // Only global unicast 2000::/3 can be a public host; this alone refuses ::, ::1, fc00::/7, fe80::/10, fec0::/10, ff00::/8, 0000::/8.
        if ((b[0] & 0xE0) != 0x20)
        {
            return false;
        }

        // 2001::/23: Teredo and IETF protocol assignments; 2001:db8::/32 and 3fff::/20 are documentation.
        if (b[0] == 0x20 && b[1] == 0x01 && (b[2] < 0x02 || (b[2] == 0x0D && b[3] == 0xB8)))
        {
            return false;
        }

        return !(b[0] == 0x3F && b[1] == 0xFF && (b[2] & 0xF0) == 0);
    }
}
