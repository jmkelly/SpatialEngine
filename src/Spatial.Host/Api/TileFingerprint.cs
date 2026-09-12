using System.Security.Cryptography;
using System.Text;

namespace Spatial.Host.Api;

/// <summary>
/// Derives the content version that makes a tile cache key content-addressed
/// (ADR-0046): a SHA-256 over the ordered parts the caller supplies (style,
/// layer/store descriptors, imagery stack and encoding options). Changing any
/// part yields a different version, so stale tiles are never served after an
/// invalidation-relevant change.
/// </summary>
internal static class TileFingerprint
{
    private const char Separator = '\u001f';

    public static string Compute(IEnumerable<string> parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(Separator, parts))));
}
