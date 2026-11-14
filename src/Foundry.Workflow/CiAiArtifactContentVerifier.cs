using System.Security.Cryptography;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Immutable-by-contract artifact bytes accepted only after the retrieved payload matches
/// the validated notice's declared size, SHA-256 digest, and expiry window.
/// </summary>
public sealed class CiAiVerifiedArtifactContent
{
    private readonly byte[] _bytes;

    internal CiAiVerifiedArtifactContent(CiAiValidatedArtifactNotice notice, byte[] bytes)
    {
        Notice = notice;
        _bytes = bytes;
    }

    /// <summary>The metadata notice whose exact dispatch provenance was validated.</summary>
    public CiAiValidatedArtifactNotice Notice { get; }

    /// <summary>Verified artifact bytes, exposed as read-only memory.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;
}

/// <summary>
/// Performs the byte-level verification step after a host-controlled retriever obtains
/// content from the notice's already-validated retrieval URI. This class performs no
/// network access and grants no authority to follow redirects or choose another origin.
/// </summary>
public static class CiAiArtifactContentVerifier
{
    /// <summary>
    /// Verifies exact content length, expiry, and SHA-256, then retains a defensive copy.
    /// </summary>
    public static CiAiVerifiedArtifactContent Verify(
        CiAiValidatedArtifactNotice notice,
        ReadOnlyMemory<byte> content,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (now == default)
            throw new ArgumentException("A current verification time is required.", nameof(now));
        if (notice.ExpiresAt <= now)
            throw new InvalidOperationException("CiAi artifact expired before content verification.");
        if (notice.SizeBytes is < 1 or > 1024 * 1024)
            throw new InvalidDataException("Validated CiAi artifact size is outside the v1 limit.");
        if (content.Length != notice.SizeBytes)
            throw new InvalidDataException("Retrieved CiAi artifact length does not match the validated manifest.");

        byte[] expectedDigest;
        try
        {
            expectedDigest = Convert.FromHexString(notice.Digest);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Validated CiAi artifact digest is not hexadecimal SHA-256.", exception);
        }

        if (expectedDigest.Length != SHA256.HashSizeInBytes)
            throw new InvalidDataException("Validated CiAi artifact digest is not a SHA-256 value.");

        var actualDigest = SHA256.HashData(content.Span);
        if (!CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest))
            throw new InvalidDataException("Retrieved CiAi artifact SHA-256 does not match the validated manifest.");

        return new CiAiVerifiedArtifactContent(notice, content.ToArray());
    }
}
