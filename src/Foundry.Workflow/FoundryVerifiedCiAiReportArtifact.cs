using System.Security.Cryptography;
using System.Text;

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
        string mediaType,
        string noticeIdentitySha256)
    {
        Request = request;
        Bytes = bytes.ToArray();
        Digest = digest;
        SizeBytes = sizeBytes;
        ExpiresAt = expiresAt;
        ProviderRunId = providerRunId;
        MediaType = mediaType;
        NoticeIdentitySha256 = noticeIdentitySha256;
    }

    public CiAiRequestTaskMappingSnapshot Request { get; }
    public ReadOnlyMemory<byte> Bytes { get; }
    public string Digest { get; }
    public long SizeBytes { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string? ProviderRunId { get; }
    public string MediaType { get; }
    /// <summary>
    /// Fingerprint of the authenticated notice, durable receipt, and outer report-artifact
    /// identity. It is deliberately distinct from the report-byte digest and nested source-artifact fingerprint.
    /// </summary>
    public string NoticeIdentitySha256 { get; }

    /// <summary>
    /// Creates an immutable content value from retrieved bytes and the exact durable ingress receipt.
    /// The receipt must correspond byte-for-byte to the authenticated, validated notice that authorized retrieval.
    /// </summary>
    public static FoundryVerifiedCiAiReportArtifact Verify(
        CiAiVerifiedArtifactContent retrieved,
        CiAiArtifactNoticeReceipt receipt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(retrieved);
        ArgumentNullException.ThrowIfNull(receipt);
        var notice = retrieved.Notice;
        ArgumentNullException.ThrowIfNull(notice);
        var mapping = notice.Request.Mapping.Validate();
        var run = notice.Request.RunBinding
            ?? throw new InvalidOperationException("CiAi report content requires an exact persisted run binding.");
        run.ValidateAgainst(mapping);
        if (notice.ExpiresAt <= now || now == default ||
            notice.SizeBytes is < 1 or > 1024 * 1024 ||
            retrieved.Bytes.IsEmpty || retrieved.Bytes.Length != notice.SizeBytes)
            throw new InvalidOperationException("CiAi report request or artifact is expired, empty, oversized, or outside its validity window.");
        if (!string.Equals(notice.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("CiAi report artifact media type must be application/json.");
        if (receipt.SchemaVersion != CiAiArtifactNoticeReceipt.CurrentSchemaVersion ||
            !string.Equals(receipt.SenderIdentity, mapping.ProviderIdentity, StringComparison.Ordinal) ||
            !string.Equals(receipt.NoticeId, notice.NoticeId, StringComparison.Ordinal) ||
            !string.Equals(receipt.IdempotencyKey, mapping.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(receipt.PayloadSha256, notice.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CiAi durable receipt does not identify the exact authenticated notice used for artifact retrieval.");

        var receiptPayloadDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(receipt.PayloadUtf8))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(receiptPayloadDigest), Convert.FromHexString(receipt.PayloadSha256)))
            throw new InvalidDataException("CiAi durable receipt payload does not match its exact-byte digest.");
        if (notice.ProviderRunId is not null &&
            !string.Equals(notice.ProviderRunId, run.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
            throw new UnauthorizedAccessException("CiAi notice provider run identity does not match the persisted run binding.");

        var reportDigest = Convert.ToHexString(SHA256.HashData(retrieved.Bytes.Span)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(reportDigest), Convert.FromHexString(notice.Digest)))
            throw new InvalidDataException("Retrieved report bytes do not match the authenticated notice's artifact digest.");

        var noticeMaterial = string.Join("\n", new[]
        {
            receipt.SenderIdentity,
            receipt.NoticeId,
            receipt.ReceiptId,
            receipt.PayloadSha256.ToLowerInvariant(),
            mapping.RequestId,
            mapping.IdempotencyKey,
            notice.ArtifactId,
            notice.ArtifactVersion,
            notice.Digest.ToLowerInvariant(),
            notice.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            notice.ExpiresAt.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        var noticeIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(noticeMaterial))).ToLowerInvariant();

        return new FoundryVerifiedCiAiReportArtifact(notice.Request, retrieved.Bytes,
            reportDigest, notice.SizeBytes, notice.ExpiresAt, notice.ProviderRunId,
            "application/json", noticeIdentity);
    }
}
