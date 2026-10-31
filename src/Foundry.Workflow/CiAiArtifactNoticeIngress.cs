using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Host boundary for authenticating the current ingress request. Implementations must
/// validate the configured token signature, issuer, audience, subject, scope, expiry,
/// and sender repository authorization before returning the registered sender identity.
/// The implementation is expected to be request-scoped; credentials never enter Foundry.
/// </summary>
public interface ICiAiArtifactNoticeAuthenticator
{
    ValueTask<string> AuthenticateSenderAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Result of an authenticated, validated, and durably receipted artifact-ready notice.
/// A receipt acknowledges notice persistence only; it does not assert artifact integrity.
/// </summary>
public sealed record CiAiArtifactNoticeIngressResult(
    CiAiArtifactNoticeReceiptOutcome Outcome,
    CiAiArtifactNoticeReceipt Receipt,
    CiAiValidatedArtifactNotice ValidatedNotice);

/// <summary>
/// Host-neutral ingress coordinator. An HTTPS adapter supplies the raw body and a
/// request-scoped authenticator. Authentication and full notice validation happen
/// before the receipt ledger is mutated.
/// </summary>
public sealed class CiAiArtifactNoticeIngress
{
    private const int MaximumPayloadBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ICiAiArtifactNoticeAuthenticator _authenticator;
    private readonly JsonlCiAiRequestTaskMappingJournal _mappings;
    private readonly CiAiArtifactNoticeValidationPolicy _policy;
    private readonly JsonlCiAiArtifactNoticeReceiptLedger _receipts;

    public CiAiArtifactNoticeIngress(
        ICiAiArtifactNoticeAuthenticator authenticator,
        JsonlCiAiRequestTaskMappingJournal mappings,
        CiAiArtifactNoticeValidationPolicy policy,
        JsonlCiAiArtifactNoticeReceiptLedger receipts)
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
        _policy = (policy ?? throw new ArgumentNullException(nameof(policy))).Validate();
        _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
    }

    public async ValueTask<CiAiArtifactNoticeIngressResult> AcceptAsync(
        ReadOnlyMemory<byte> exactRequestBody,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (receivedAt == default)
            throw new ArgumentException("A request receipt timestamp is required.", nameof(receivedAt));
        if (exactRequestBody.IsEmpty || exactRequestBody.Length > MaximumPayloadBytes)
            throw new ArgumentException("The exact request body must be non-empty and no larger than 64 KiB.", nameof(exactRequestBody));

        var senderIdentity = await _authenticator.AuthenticateSenderAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(senderIdentity) || senderIdentity.Length > 128 ||
            senderIdentity.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new UnauthorizedAccessException("The host authenticator did not return a valid registered sender identity.");

        string payload;
        try
        {
            payload = StrictUtf8.GetString(exactRequestBody.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new FormatException("CiAi notice body must contain valid UTF-8 bytes.", exception);
        }

        var (noticeId, idempotencyKey) = ReadReceiptIdentity(payload);
        var notice = new CiAiArtifactReadyNotice(senderIdentity, noticeId, idempotencyKey, payload).Validate();
        var validated = CiAiArtifactReadyNoticeValidator.Validate(notice, _mappings, _policy, receivedAt);
        cancellationToken.ThrowIfCancellationRequested();

        var (outcome, receipt) = _receipts.Accept(notice, receivedAt);
        return new CiAiArtifactNoticeIngressResult(outcome, receipt, validated);
    }

    private static (string NoticeId, string IdempotencyKey) ReadReceiptIdentity(string payload)
    {
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("noticeId", out var noticeIdElement) ||
            noticeIdElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("manifest", out var manifest) ||
            manifest.ValueKind != JsonValueKind.Object ||
            !manifest.TryGetProperty("idempotencyKey", out var keyElement) ||
            keyElement.ValueKind != JsonValueKind.String)
            throw new FormatException("CiAi notice must contain string noticeId and manifest.idempotencyKey values.");

        return (noticeIdElement.GetString()!, keyElement.GetString()!);
    }
}
