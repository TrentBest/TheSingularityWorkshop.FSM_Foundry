using System.Globalization;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Trusted host configuration for validating a CiAi artifact-ready notice.
/// These values must come from provider registration, never from the notice.
/// </summary>
public sealed record CiAiArtifactNoticeValidationPolicy(
    string ReceiverIdentity,
    string ProviderRepository,
    string CapabilityIdentity,
    string CapabilityVersion,
    string ImplementationRevision)
{
    public CiAiArtifactNoticeValidationPolicy Validate()
    {
        static bool Clean(string? value, int maximum = 256) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
            !value.Any(character => char.IsControl(character));

        if (!Clean(ReceiverIdentity, 128) || !Clean(CapabilityIdentity) ||
            !Clean(CapabilityVersion, 128) || !Clean(ImplementationRevision, 256))
            throw new ArgumentException("CiAi receiver and capability registration values are required.");

        var repositoryParts = ProviderRepository?.Split('/') ?? Array.Empty<string>();
        if (repositoryParts.Length != 2 || repositoryParts.Any(part => !Clean(part, 100)))
            throw new ArgumentException("Provider repository must use owner/name identity.", nameof(ProviderRepository));

        return this;
    }
}

/// <summary>
/// A notice that has passed host identity, exact dispatch mapping, and manifest checks.
/// It is still only metadata; artifact retrieval and byte-level verification remain separate.
/// </summary>
public sealed record CiAiValidatedArtifactNotice(
    CiAiRequestTaskMappingSnapshot Request,
    string NoticeId,
    DateTimeOffset NotifiedAt,
    string ProviderRepository,
    string CapabilityIdentity,
    string CapabilityVersion,
    string ImplementationRevision,
    string ArtifactId,
    string ArtifactVersion,
    string MediaType,
    long SizeBytes,
    Uri RetrievalUri,
    string Digest,
    DateTimeOffset ExpiresAt,
    string? ProviderRunId,
    string ClientIdentity,
    string PayloadSha256);

/// <summary>
/// Validates the v1 metadata-only ready notice against trusted provider registration and
/// the exact, already-bound Foundry dispatch intent. This does not authenticate HTTP,
/// retrieve an artifact, or establish that the declared digest matches artifact bytes.
/// </summary>
public static class CiAiArtifactReadyNoticeValidator
{
    private static readonly HashSet<string> NoticeProperties = new(StringComparer.Ordinal)
    {
        "contractVersion", "noticeId", "recipientIdentity", "notifiedAt", "manifest"
    };

    private static readonly HashSet<string> ManifestProperties = new(StringComparer.Ordinal)
    {
        "requestId", "operationId", "idempotencyKey", "providerRepository",
        "capabilityIdentity", "capabilityVersion", "implementationRevision",
        "artifactId", "artifactVersion", "mediaType", "sizeBytes", "retrievalUri",
        "digest", "expiresAt", "sourceSha", "clientIdentity"
    };

    public static CiAiValidatedArtifactNotice Validate(
        CiAiArtifactReadyNotice notice,
        JsonlCiAiRequestTaskMappingJournal mappings,
        CiAiArtifactNoticeValidationPolicy policy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (now == default)
            throw new ArgumentException("A current validation time is required.", nameof(now));

        notice.Validate();
        if (!string.Equals(notice.SenderIdentity, GetExpectedSender(mappings, notice, now), StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Authenticated CiAi sender does not match the persisted provider registration.");

        using var document = JsonDocument.Parse(notice.PayloadUtf8, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8
        });

        var root = document.RootElement;
        RequireObject(root, "notice");
        RequireExactProperties(root, NoticeProperties, optional: null);

        if (ReadInt32(root, "contractVersion") != 1)
            throw new FormatException("Unsupported CiAi artifact-ready notice contract version.");

        var noticeId = ReadText(root, "noticeId", 128);
        if (!string.Equals(noticeId, notice.NoticeId, StringComparison.Ordinal))
            throw new InvalidOperationException("Notice identity does not match the authenticated receipt identity.");

        var recipientIdentity = ReadText(root, "recipientIdentity", 128);
        if (!string.Equals(recipientIdentity, policy.ReceiverIdentity, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("CiAi notice recipient does not match this Foundry host.");

        var notifiedAt = ReadTimestamp(root, "notifiedAt");
        var manifest = root.GetProperty("manifest");
        RequireObject(manifest, "manifest");
        RequireExactProperties(manifest, ManifestProperties, new HashSet<string>(StringComparer.Ordinal) { "providerRunId" });

        var requestId = ReadText(manifest, "requestId", 128);
        var snapshot = mappings.GetActionable(requestId, now);
        var mapping = snapshot.Mapping;

        if (!string.Equals(notice.SenderIdentity, mapping.ProviderIdentity, StringComparison.Ordinal) ||
            !string.Equals(ReadText(manifest, "operationId", 128), mapping.OperationId, StringComparison.Ordinal) ||
            !string.Equals(ReadText(manifest, "idempotencyKey", 128), mapping.IdempotencyKey, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("CiAi notice does not match the authenticated provider and exact persisted request identity.");

        if (notifiedAt < mapping.CreatedAt || notifiedAt > mapping.ExpiresAt || notifiedAt > now)
            throw new InvalidOperationException("CiAi notice timestamp is outside the active dispatch request window.");

        var providerRepository = ReadRepository(manifest, "providerRepository");
        if (!string.Equals(providerRepository, policy.ProviderRepository, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CiAi provider repository is not the registered evidence producer.");

        var capabilityIdentity = ReadText(manifest, "capabilityIdentity", 256);
        var capabilityVersion = ReadText(manifest, "capabilityVersion", 128);
        var implementationRevision = ReadText(manifest, "implementationRevision", 256);
        if (!string.Equals(capabilityIdentity, policy.CapabilityIdentity, StringComparison.Ordinal) ||
            !string.Equals(capabilityVersion, policy.CapabilityVersion, StringComparison.Ordinal) ||
            !string.Equals(implementationRevision, policy.ImplementationRevision, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("CiAi capability identity, version, or implementation revision is not registered.");

        var artifactId = ReadText(manifest, "artifactId", 256);
        var artifactVersion = ReadText(manifest, "artifactVersion", 128);
        var mediaType = ReadText(manifest, "mediaType", 128);
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("CiAi evidence artifact media type must be application/json.");

        var sizeBytes = ReadInt64(manifest, "sizeBytes");
        if (sizeBytes is < 1 or > 16 * 1024 * 1024)
            throw new FormatException("CiAi evidence artifact size is outside the 16 MiB v1 limit.");

        var retrievalUri = ReadRetrievalUri(manifest);
        var configuredOrigin = new Uri(mapping.ArtifactOrigin, UriKind.Absolute);
        if (!string.Equals(retrievalUri.GetLeftPart(UriPartial.Authority),
                configuredOrigin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CiAi artifact retrieval URI is outside the request's registered provider origin.");

        var digest = ReadText(manifest, "digest", 64);
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit))
            throw new FormatException("CiAi artifact digest must be a 64-character SHA-256 hexadecimal value.");

        var expiresAt = ReadTimestamp(manifest, "expiresAt");
        if (expiresAt <= now)
            throw new InvalidOperationException("CiAi artifact manifest has expired.");

        var sourceSha = ReadText(manifest, "sourceSha", 40);
        if (sourceSha.Length != 40 || !sourceSha.All(Uri.IsHexDigit) ||
            !string.Equals(sourceSha, mapping.CandidateSha, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CiAi artifact source SHA does not match the exact dispatched candidate.");

        var clientIdentity = ReadText(manifest, "clientIdentity", 128);
        if (!string.Equals(clientIdentity, policy.ReceiverIdentity, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("CiAi artifact client identity does not match this Foundry host.");

        string? providerRunId = null;
        if (manifest.TryGetProperty("providerRunId", out var providerRunElement) &&
            providerRunElement.ValueKind != JsonValueKind.Null)
        {
            if (providerRunElement.ValueKind != JsonValueKind.String)
                throw new FormatException("CiAi providerRunId must be a string when supplied.");
            providerRunId = providerRunElement.GetString();
            ValidateText(providerRunId, 128, "providerRunId");
        }

        return new CiAiValidatedArtifactNotice(
            snapshot, noticeId, notifiedAt, providerRepository, capabilityIdentity,
            capabilityVersion, implementationRevision, artifactId, artifactVersion,
            mediaType, sizeBytes, retrievalUri, digest.ToLowerInvariant(), expiresAt,
            providerRunId, clientIdentity, notice.PayloadSha256);
    }

    private static string GetExpectedSender(
        JsonlCiAiRequestTaskMappingJournal mappings,
        CiAiArtifactReadyNotice notice,
        DateTimeOffset now)
    {
        var snapshot = mappings.GetActionable(
            ReadRequestId(notice.PayloadUtf8), now);
        return snapshot.Mapping.ProviderIdentity;
    }

    private static string ReadRequestId(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("manifest", out var manifest) ||
            manifest.ValueKind != JsonValueKind.Object)
            throw new FormatException("CiAi notice manifest is missing.");
        return ReadText(manifest, "requestId", 128);
    }

    private static void RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException($"CiAi {name} must be a JSON object.");
    }

    private static void RequireExactProperties(
        JsonElement element,
        HashSet<string> required,
        HashSet<string>? optional)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if ((!required.Contains(property.Name) && optional?.Contains(property.Name) != true) ||
                !found.Add(property.Name))
                throw new FormatException("CiAi notice contains an unknown or duplicate property.");
        }

        if (required.Any(name => !found.Contains(name)))
            throw new FormatException("CiAi notice is missing one or more required properties.");
    }

    private static string ReadRepository(JsonElement element, string name)
    {
        var value = ReadText(element, name, 201);
        var parts = value.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new FormatException("CiAi provider repository must use owner/name identity.");
        return value;
    }

    private static string ReadText(JsonElement element, string name, int maximumLength)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            throw new FormatException($"CiAi notice property '{name}' must be a string.");
        var value = property.GetString();
        ValidateText(value, maximumLength, name);
        return value!;
    }

    private static void ValidateText(string? value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => char.IsControl(character)))
            throw new FormatException($"CiAi notice property '{name}' is empty or outside its allowed bounds.");
    }

    private static int ReadInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
            throw new FormatException($"CiAi notice property '{name}' must be an integer.");
        return value;
    }

    private static long ReadInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var value))
            throw new FormatException($"CiAi notice property '{name}' must be an integer.");
        return value;
    }

    private static DateTimeOffset ReadTimestamp(JsonElement element, string name)
    {
        var value = ReadText(element, name, 64);
        var timeSeparator = value.IndexOf('T');
        var hasExplicitZone = value.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ||
            (timeSeparator >= 0 &&
             (value.IndexOf('+', timeSeparator) >= 0 || value.IndexOf('-', timeSeparator) >= 0));
        if (!hasExplicitZone || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp))
            throw new FormatException($"CiAi notice property '{name}' must be an ISO-8601 timestamp with an explicit UTC offset.");
        return timestamp;
    }

    private static Uri ReadRetrievalUri(JsonElement element)
    {
        var value = ReadText(element, "retrievalUri", 2048);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new FormatException("CiAi retrieval URI must be credential-free HTTPS on port 443 without query or fragment.");
        return uri;
    }
}
