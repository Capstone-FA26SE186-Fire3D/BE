using System.Net;
using System.Net.Sockets;

namespace Fire3D.Infrastructure.Ai;

/// <summary>The address behind a source URL is not a public one (loopback, private, link-local, reserved).</summary>
public sealed class BlockedAddressException(string message) : Exception(message);

/// <summary>
/// Keeps the verifier's outbound requests on the public internet (BE#188). The check runs on the address the socket really connects to,
/// so a DNS answer or a redirect cannot lead to an internal host such as the instance metadata service.
/// </summary>
internal static class PublicAddressGuard
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)            // carrier-grade NAT 100.64/10
                || (b[0] == 169 && b[1] == 254)                          // link-local, cloud metadata
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)          // 192.0.0/24, 192.0.2/24
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 198 && (b[1] is 18 or 19))                   // benchmarking
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)
                || b[0] >= 224);                                         // multicast, reserved, broadcast
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6Any)
                || (b[0] & 0xFE) == 0xFC                                  // unique local fc00::/7
                || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)                // link-local fe80::/10
                || b[0] == 0xFF                                           // multicast
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)   // documentation 2001:db8::/32
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)   // Teredo
                || (b[0] == 0x20 && b[1] == 0x02)                         // 6to4
                || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B));  // NAT64
        }
        return false;
    }

    /// <summary>SocketsHttpHandler.ConnectCallback: resolve, refuse the whole host if any answer is not public, then connect.</summary>
    public static ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct) => ConnectAsync(context.DnsEndPoint, ct);

    internal static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken ct)
    {
        var host = endpoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a)))
            throw new BlockedAddressException("The source address is not a public internet address.");
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { socket.Dispose(); last = ex; }
        }
        throw new HttpRequestException("Could not connect to the source address.", last);
    }
}
