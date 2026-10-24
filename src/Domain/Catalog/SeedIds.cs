using System.Security.Cryptography;
using System.Text;

namespace Foodplanner.Domain.Catalog;

/// <summary>
/// Fixed ids for seed rows: a name-based (version 5, SHA-1) GUID of the kind and the row's stable key. The same key always
/// gives the same id, in every environment, so the seed migration is idempotent by construction and renaming or
/// translating a row's display name does not change its id. Never change a key once it has shipped.
/// </summary>
public static class SeedIds
{
    private static readonly Guid Namespace = new("6f1d3c2a-5b7e-4a49-9c0d-2f8e1b7a4c10");

    public static Guid For(string kind, string key) => NameBased(Namespace, $"{kind}:{key}");

    /// <summary>RFC 4122 version 5 (SHA-1) name-based GUID. Public so the algorithm can be tested against the RFC's published vector.</summary>
    public static Guid NameBased(Guid @namespace, string name)
    {
        var namespaceBytes = @namespace.ToByteArray();
        SwapByteOrder(namespaceBytes);
        var hash = SHA1.HashData([.. namespaceBytes, .. Encoding.UTF8.GetBytes(name)]);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        SwapByteOrder(bytes);
        return new Guid(bytes);
    }

    // Guid.ToByteArray is little-endian for the first three fields; RFC 4122 hashing wants network order.
    private static void SwapByteOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}
