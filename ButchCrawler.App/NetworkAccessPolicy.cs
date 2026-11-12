using System.Net;
using System.Net.Sockets;

namespace ButchCrawler.App;

// Resolve and connect to the same validated address to prevent DNS rebinding.
public static class NetworkAccessPolicy
{
    public static async Task<bool> IsPublicUriAsync(string value, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
        if (uri.IsLoopback) return false;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct).ConfigureAwait(false);
            return addresses.Length > 0 && addresses.All(IsPublicAddress);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127 ||
                (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
                (bytes[0] == 169 && bytes[1] == 254) ||
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) || bytes[0] >= 224);
        // Only global unicast IPv6; exclude transition ranges with embedded IPv4.
        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            (bytes[0] & 0xe0) == 0x20 &&
            !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0);
    }

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(a => !IsPublicAddress(a)))
            throw new HttpRequestException("Destination resolves to a non-public network address");
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) { socket.Dispose(); throw; }
            catch (Exception ex) { socket.Dispose(); last = ex; }
        }
        throw new HttpRequestException("Unable to connect to destination", last);
    }
}
