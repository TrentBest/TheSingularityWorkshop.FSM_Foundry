using System.Net;
using System.Net.Sockets;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Creates a direct HTTPS client for one registered artifact origin. Every new connection
/// resolves the hostname again, rejects the entire answer set if any address is non-public,
/// and connects to a vetted IP address while HttpClient retains the original hostname for
/// TLS certificate validation and SNI. Redirects and ambient proxy routing are disabled.
/// </summary>
public static class PinnedHttpsArtifactHttpClientFactory
{
    public static HttpClient Create(Uri registeredOrigin, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(registeredOrigin);
        if (!registeredOrigin.IsAbsoluteUri ||
            !string.Equals(registeredOrigin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            registeredOrigin.Port != 443 ||
            !string.IsNullOrEmpty(registeredOrigin.UserInfo) ||
            !string.IsNullOrEmpty(registeredOrigin.Query) ||
            !string.IsNullOrEmpty(registeredOrigin.Fragment) ||
            registeredOrigin.AbsolutePath != "/" ||
            IPAddress.TryParse(registeredOrigin.Host, out _))
            throw new ArgumentException("Registered artifact origin must be a DNS hostname on HTTPS port 443 with no path, credentials, query, or fragment.", nameof(registeredOrigin));

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectCallback = async (context, cancellationToken) =>
                await ConnectPinnedAsync(context.DnsEndPoint, registeredOrigin.IdnHost, cancellationToken)
                    .ConfigureAwait(false)
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// Returns true only for globally routable unicast addresses. This deliberately rejects
    /// special-purpose, private, loopback, link-local, multicast, documentation, and transition
    /// ranges rather than attempting to maintain a permissive exception list.
    /// </summary>
    public static bool IsPubliclyRoutable(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback) ||
            address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
            return false;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0];
            var b = bytes[1];
            var c = bytes[2];

            if (a == 0 || a == 10 || a == 127 || a >= 224 ||
                (a == 100 && b is >= 64 and <= 127) ||
                (a == 169 && b == 254) ||
                (a == 172 && b is >= 16 and <= 31) ||
                (a == 192 && (b == 0 || b == 168)) ||
                (a == 192 && b == 88 && c == 99) ||
                (a == 198 && (b == 18 || b == 19 || b == 51)) ||
                (a == 203 && b == 0 && c == 113))
                return false;

            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        if ((bytes[0] & 0xE0) != 0x20)
            return false;
        if (bytes[0] == 0x20 && bytes[1] == 0x01 &&
            ((bytes[2] & 0xFE) == 0x00 || bytes[2] == 0x0D && bytes[3] == 0xB8))
            return false;
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return false;
        if (bytes[0] == 0x3F && bytes[1] == 0xFF && (bytes[2] & 0xF0) == 0x00)
            return false;

        return true;
    }

    private static async ValueTask<Stream> ConnectPinnedAsync(
        DnsEndPoint endpoint,
        string registeredHost,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(endpoint.Host, registeredHost, StringComparison.OrdinalIgnoreCase) ||
            endpoint.Port != 443)
            throw new HttpRequestException("Artifact connection target differs from the registered HTTPS origin.");

        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken)
            .ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsPubliclyRoutable(address)))
            throw new HttpRequestException("Artifact hostname resolved to an empty or non-public address set.");

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = exception;
                if (exception is OperationCanceledException)
                    throw;
            }
        }

        throw new HttpRequestException("Unable to connect to any vetted address for the registered artifact origin.", lastError);
    }
}
