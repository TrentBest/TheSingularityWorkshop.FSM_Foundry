using System.Net;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Concrete CiAi artifact transport backed exclusively by clients created for explicitly
/// registered HTTPS origins. Each client uses connection-time DNS/IP pinning and disables
/// redirects, proxies, cookies, and automatic decompression. Requests are sent to the supplied
/// absolute URI unchanged; credentials are deliberately not attached or forwarded.
/// </summary>
public sealed class PinnedHttpsCiAiArtifactTransport : ICiAiArtifactTransport, IDisposable
{
    private readonly IReadOnlyDictionary<string, HttpClient> _clients;
    private readonly bool _ownsClients;
    private bool _disposed;

    private PinnedHttpsCiAiArtifactTransport(
        IReadOnlyDictionary<string, HttpClient> clients,
        bool ownsClients)
    {
        _clients = clients;
        _ownsClients = ownsClients;
    }

    /// <summary>
    /// Creates one independently pinned client for each registered HTTPS DNS origin.
    /// Redirect destinations must be registered separately and retrieved with a separate
    /// request; this transport never follows redirects or forwards source-origin credentials.
    /// </summary>
    public static PinnedHttpsCiAiArtifactTransport Create(
        IEnumerable<Uri> registeredOrigins,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(registeredOrigins);
        var clients = new Dictionary<string, HttpClient>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var origin in registeredOrigins)
            {
                ArgumentNullException.ThrowIfNull(origin);
                var key = ValidateAndGetOrigin(origin);
                if (clients.ContainsKey(key))
                    throw new ArgumentException("Registered artifact origins must be unique.", nameof(registeredOrigins));
                clients.Add(key, PinnedHttpsArtifactHttpClientFactory.Create(origin, timeout));
            }

            if (clients.Count == 0)
                throw new ArgumentException("At least one registered artifact origin is required.", nameof(registeredOrigins));

            return new PinnedHttpsCiAiArtifactTransport(clients, ownsClients: true);
        }
        catch
        {
            foreach (var client in clients.Values)
                client.Dispose();
            throw;
        }
    }

    public async ValueTask<CiAiArtifactTransportResponse> GetAsync(
        Uri validatedRetrievalUri,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(validatedRetrievalUri);

        var origin = ValidateAndGetRequestOrigin(validatedRetrievalUri);
        if (!_clients.TryGetValue(origin, out var client))
            throw new UnauthorizedAccessException("CiAi artifact URI is outside the explicitly registered pinned HTTPS origins.");

        using var request = new HttpRequestMessage(HttpMethod.Get, validatedRetrievalUri);
        request.Headers.Accept.ParseAdd("application/json");
        var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        try
        {
            var finalUri = response.RequestMessage?.RequestUri ?? validatedRetrievalUri;
            var redirected = (int)response.StatusCode is >= 300 and < 400 ||
                !Uri.Equals(finalUri, validatedRetrievalUri);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new CiAiArtifactTransportResponse(
                response.StatusCode,
                mediaType,
                finalUri,
                redirected,
                new ResponseOwnedStream(content, response));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsClients)
        {
            foreach (var client in _clients.Values)
                client.Dispose();
        }
    }

    private static string ValidateAndGetOrigin(Uri origin)
    {
        if (!origin.IsAbsoluteUri ||
            !string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            origin.Port != 443 ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment) ||
            origin.AbsolutePath != "/" ||
            IPAddress.TryParse(origin.Host, out _))
            throw new ArgumentException("Registered artifact origin must be a DNS hostname on HTTPS port 443.", nameof(origin));
        return origin.GetLeftPart(UriPartial.Authority);
    }

    private static string ValidateAndGetRequestOrigin(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            IPAddress.TryParse(uri.Host, out _))
            throw new UnauthorizedAccessException("CiAi artifact retrieval requires an absolute HTTPS URI on port 443 with a DNS hostname.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private sealed class ResponseOwnedStream(Stream inner, HttpResponseMessage response) : Stream
    {
        private bool _disposed;

        public override bool CanRead => !_disposed && inner.CanRead;
        public override bool CanSeek => !_disposed && inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await inner.DisposeAsync().ConfigureAwait(false);
                response.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }
}
