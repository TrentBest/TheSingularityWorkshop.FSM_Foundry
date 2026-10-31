using System.Globalization;
using System.Net;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// A response from a host-owned artifact transport. The transport must disable automatic
/// redirects and enforce DNS/IP restrictions at connection time; the coordinator independently
/// rejects redirect metadata and any final URI other than the validated request URI.
/// </summary>
public sealed class CiAiArtifactTransportResponse : IAsyncDisposable
{
    public CiAiArtifactTransportResponse(
        HttpStatusCode statusCode,
        string mediaType,
        Uri finalUri,
        bool redirected,
        Stream content)
    {
        StatusCode = statusCode;
        MediaType = mediaType ?? throw new ArgumentNullException(nameof(mediaType));
        FinalUri = finalUri ?? throw new ArgumentNullException(nameof(finalUri));
        Redirected = redirected;
        Content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public HttpStatusCode StatusCode { get; }
    public string MediaType { get; }
    public Uri FinalUri { get; }
    public bool Redirected { get; }
    public Stream Content { get; }

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// Host-owned network boundary for retrieving a previously validated artifact.
/// Implementations must use the supplied URI unchanged, disable redirects, and enforce
/// public-address DNS/IP policy on the actual connected address (including rebinding defense).
/// </summary>
public interface ICiAiArtifactTransport
{
    ValueTask<CiAiArtifactTransportResponse> GetAsync(
        Uri validatedRetrievalUri,
        CancellationToken cancellationToken);
}

/// <summary>
/// Retrieves and verifies one artifact using a host-supplied, security-hardened transport.
/// Foundry bounds the response stream, validates response metadata, and verifies exact bytes.
/// It does not resolve DNS, open sockets, or implement HTTP itself.
/// </summary>
public sealed class CiAiArtifactRetrievalCoordinator
{
    private const int MaximumArtifactBytes = 16 * 1024 * 1024;
    private readonly ICiAiArtifactTransport _transport;
    private readonly TimeSpan _timeout;

    public CiAiArtifactRetrievalCoordinator(
        ICiAiArtifactTransport transport,
        TimeSpan? timeout = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Artifact retrieval timeout must be positive and no greater than five minutes.");
    }

    /// <summary>
    /// Retrieves only the validated URI, rejects redirects and unexpected response metadata,
    /// reads no more than declared size plus one byte, and verifies size/expiry/SHA-256.
    /// </summary>
    public async ValueTask<CiAiVerifiedArtifactContent> RetrieveAndVerifyAsync(
        CiAiValidatedArtifactNotice notice,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (now == default)
            throw new ArgumentException("A current retrieval time is required.", nameof(now));
        if (notice.ExpiresAt <= now)
            throw new InvalidOperationException("CiAi artifact expired before retrieval.");
        if (notice.SizeBytes is < 1 or > MaximumArtifactBytes)
            throw new InvalidOperationException("Validated CiAi artifact size is outside the v1 limit.");
        if (notice.RetrievalUri.Scheme != Uri.UriSchemeHttps || notice.RetrievalUri.Port != 443 ||
            !string.IsNullOrEmpty(notice.RetrievalUri.UserInfo) ||
            !string.IsNullOrEmpty(notice.RetrievalUri.Query) ||
            !string.IsNullOrEmpty(notice.RetrievalUri.Fragment))
            throw new InvalidOperationException("Validated CiAi retrieval URI no longer satisfies the HTTPS origin policy.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        await using var response = await _transport.GetAsync(notice.RetrievalUri, timeout.Token)
            .ConfigureAwait(false);

        if (response.Redirected || !response.FinalUri.Equals(notice.RetrievalUri))
            throw new InvalidDataException("CiAi artifact transport followed a redirect or changed the validated retrieval URI.");
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidDataException("CiAi artifact endpoint did not return HTTP 200.");
        if (!string.Equals(response.MediaType.Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("CiAi artifact response media type must be application/json.");
        if (!response.Content.CanRead)
            throw new InvalidDataException("CiAi artifact response stream is not readable.");

        var bytes = await ReadBoundedAsync(response.Content, notice.SizeBytes, timeout.Token)
            .ConfigureAwait(false);
        return CiAiArtifactContentVerifier.Verify(notice, bytes, now);
    }

    private static async ValueTask<byte[]> ReadBoundedAsync(
        Stream content,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        var limit = checked((int)expectedSize + 1);
        using var buffer = new MemoryStream((int)expectedSize);
        var chunk = new byte[81920];
        while (true)
        {
            var remaining = limit - (int)buffer.Length;
            if (remaining <= 0)
                throw new InvalidDataException("CiAi artifact response exceeded its declared size.");
            var read = await content.ReadAsync(
                chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            buffer.Write(chunk, 0, read);
            if (buffer.Length > expectedSize)
                throw new InvalidDataException("CiAi artifact response exceeded its declared size.");
        }

        return buffer.ToArray();
    }
}
