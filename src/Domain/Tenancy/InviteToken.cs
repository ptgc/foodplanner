using System.Security.Cryptography;
using System.Text;

namespace Foodplanner.Domain.Tenancy;

/// <summary>Invite bearer tokens: 32 random bytes, base64url. Only the SHA-256 hash is ever stored.</summary>
public static class InviteToken
{
    public const int ByteLength = 32;

    /// <summary>Length of every token we issue (32 bytes, base64url, no padding).</summary>
    public const int EncodedLength = 43;

    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>False for anything that cannot be a token we issued, so lookups never hash arbitrary long input.</summary>
    public static bool HasValidShape(string? token) =>
        token is { Length: EncodedLength } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
