using System.Net;
using System.Net.Sockets;

namespace wow.tools.local.Services
{
    internal static class SafeRemoteFile
    {
        private const int MaxRedirects = 5;
        private static readonly HttpClient WebClient = CreateClient();

        public static async Task<byte[]> DownloadAsync(string url, long maxBytes, CancellationToken cancellationToken = default)
        {
            if (maxBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));

            var currentUri = ValidateUri(url);
            for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                using var response = await WebClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirectCount == MaxRedirects)
                        throw new HttpRequestException("Remote file exceeded the redirect limit.");

                    var location = response.Headers.Location ?? throw new HttpRequestException("Remote file redirect did not include a destination.");
                    currentUri = ValidateUri(location.IsAbsoluteUri ? location : new Uri(currentUri, location));
                    continue;
                }

                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
                    throw new InvalidDataException($"Remote file exceeds the {maxBytes}-byte size limit.");

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var output = new MemoryStream();
                var buffer = new byte[81920];
                long totalBytes = 0;

                while (true)
                {
                    var bytesRead = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (bytesRead == 0)
                        break;

                    totalBytes += bytesRead;
                    if (totalBytes > maxBytes)
                        throw new InvalidDataException($"Remote file exceeds the {maxBytes}-byte size limit.");

                    await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }

                return output.ToArray();
            }

            throw new HttpRequestException("Remote file could not be downloaded.");
        }

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                // A proxy would resolve the attacker-controlled destination itself, bypassing address pinning.
                UseProxy = false,
                ConnectCallback = ConnectToPublicAddressAsync,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };

            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
        }

        private static Uri ValidateUri(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                throw new InvalidDataException("Remote file URL must be absolute.");

            return ValidateUri(uri);
        }

        private static Uri ValidateUri(Uri uri)
        {
            if ((uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host))
                throw new InvalidDataException("Remote file URL must use HTTP or HTTPS and include a host.");

            if (!string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidDataException("Remote file URL must not include a fragment.");

            return uri;
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.MovedPermanently
                || statusCode == HttpStatusCode.Redirect
                || statusCode == HttpStatusCode.RedirectMethod
                || statusCode == HttpStatusCode.TemporaryRedirect
                || statusCode == HttpStatusCode.PermanentRedirect;
        }

        private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            var publicAddresses = addresses.Where(IsPublicAddress).ToArray();
            if (publicAddresses.Length == 0)
                throw new HttpRequestException("Remote file host did not resolve to a public address.");

            Exception? lastError = null;
            foreach (var address in publicAddresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
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

            throw new HttpRequestException("Remote file host could not be reached through a public address.", lastError);
        }

        internal static bool IsPublicAddress(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            if (IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.Any)
                || address.Equals(IPAddress.None)
                || address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.IPv6None))
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
                return IsPublicIPv4(bytes);

            if (address.AddressFamily != AddressFamily.InterNetworkV6
                || address.IsIPv6LinkLocal
                || address.IsIPv6Multicast
                || address.IsIPv6SiteLocal
                || address.IsIPv6Teredo
                || (bytes[0] & 0xfe) == 0xfc
                || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8))
            {
                return false;
            }

            // Reject private IPv4 addresses embedded in compatible, NAT64, and 6to4 forms.
            if (bytes[..12].SequenceEqual(new byte[12])
                || (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes[4..12].SequenceEqual(new byte[8])))
            {
                return IsPublicIPv4(bytes[12..16]);
            }

            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes[4] == 0x00 && bytes[5] == 0x01)
                return false;

            if (bytes[0] == 0x20 && bytes[1] == 0x02)
                return IsPublicIPv4(bytes[2..6]);

            return true;
        }

        private static bool IsPublicIPv4(ReadOnlySpan<byte> bytes)
        {
            return bytes.Length == 4
                && bytes[0] != 0
                && bytes[0] != 10
                && bytes[0] != 127
                && !(bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
                && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                && bytes[0] < 224;
        }
    }
}
