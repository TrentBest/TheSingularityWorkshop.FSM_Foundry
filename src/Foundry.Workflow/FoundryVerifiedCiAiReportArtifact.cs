using System.Security.Cryptography;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Foundry-owned verified JSON artifact content. The host may create this only after
/// authenticating and validating the CiAi notice against the persisted request mapping,
/// then retrieving the artifact through its policy-enforcing transport.
/// This factory verifies the retrieved bytes; it does not authenticate the sender or
/// validate the notice on the host's behalf.
/// </summary>
public sealed class FoundryVerifiedCiAiReportArtifact
{
    private FoundryVerifiedCiAiReportArtifact(
        CiAiRequestTaskMappingSnapshot request,
        ReadOnlyMemory<byte> bytes,
        string digest,
        long sizeBytes,
        DateTimeOffset expiresAt,
        string? providerRunId,
        string mediaType)
    {
        Request = request;
        Bytes = bytes.ToArray();
        Digest = digest;
        SizeBytes = sizeBytes;
        ExpiresAt = expiresAt;
        ProviderRunId = providerRunId;
        MediaType = mediaType;
    }

    public CiAiRequestTaskMappingSnapshot Request { get; }
    public ReadOnlyMemory<byte> Bytes { get; }
    public string Digest { get; }
    public long SizeBytes { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string? ProviderRunId { get; }
    public string MediaType { get; }

    /// <summary>
    /// Creates an immutable content value after host-side notice validation and retrieval.
    /// Expected digest, size, expiry, and optional provider run ID must come from the
    /// already-validated notice, never from the downloaded JSON body.
    /// </summary>
    public static FoundryVerifiedCiAiReportArtifact Verify(
        CiAiRequestTaskMappingSnapshot request,
        ReadOnlyMemory<byte> bytes,
        string mediaType,
        string expectedDigest,
        long expectedSizeBytes,
        DateTimeOffset expiresAt,
        string? providerRunId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mapping = request.Mapping.Validate();
        var run = request.RunBinding
            ?? throw new InvalidOperationException("CiAi report content requires an exact persisted run binding.");
        run.ValidateAgainst(mapping);
        if (request.Cancelled || now == default || now < mapping.CreatedAt ||
            now > mapping.ExpiresAt || expiresAt <= now)
            throw new InvalidOperationException("CiAi report request or artifact is cancelled, expired, or outside its validity window.");
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("CiAi report artifact media type must be application/json.");
        if (expectedSizeBytes is < 1 or > 1024 * 1024 ||
            bytes.IsEmpty || bytes.Length != expectedSizeBytes)
            throw new InvalidDataException("CiAi report artifact is empty, oversized, or has an unexpected byte length.");
        if (expectedDigest is not { Length: 64 } || !expectedDigest.All(Uri.IsHexDigit))
            throw new FormatException("CiAi report artifact digest must be a 64-character SHA-256 value.");
        var actualDigest = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualDigest), Convert.FromHexString(expectedDigest)))
            throw new InvalidDataException("CiAi report artifact content does not match the validated notice digest.");
        if (providerRunId is not null &&
            !string.Equals(providerRunId, run.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
            throw new UnauthorizedAccessException("CiAi notice provider run identity does not match the persisted run binding.");

        return new FoundryVerifiedCiAiReportArtifact(request, bytes,
            actualDigest, expectedSizeBytes, expiresAt, providerRunId, "application/json");
    }
}
