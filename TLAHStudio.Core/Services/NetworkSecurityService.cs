using System.Net;
using System.Net.Sockets;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services;

public interface INetworkSecurityService
{
    Task<Uri> ValidateAsync(
        string url,
        ToolPlatformSettings settings,
        CancellationToken ct = default,
        bool bypassRestrictions = false);
}

public sealed class NetworkSecurityService : INetworkSecurityService
{
    public async Task<Uri> ValidateAsync(
        string url,
        ToolPlatformSettings settings,
        CancellationToken ct = default,
        bool bypassRestrictions = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("A valid absolute URL is required.");
        if (!bypassRestrictions && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Tool network requests require HTTPS.");
        if (!bypassRestrictions && !ToolPlatformService.MatchesDomainList(settings.NetworkAllowlist, uri.IdnHost))
            throw new InvalidOperationException($"Domain is not in the tool allowlist: {uri.IdnHost}");

        if (IPAddress.TryParse(uri.IdnHost, out var literal))
        {
            if (!bypassRestrictions && IsPrivateOrLocal(literal))
                throw new InvalidOperationException("Private, loopback, and link-local network targets are blocked.");
            return uri;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, ct);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"Unable to resolve {uri.IdnHost}: {ex.Message}", ex);
        }

        if (!bypassRestrictions && (addresses.Length == 0 || addresses.Any(IsPrivateOrLocal)))
            throw new InvalidOperationException("The domain resolves to a private, loopback, or link-local address.");
        return uri;
    }

    internal static bool IsPrivateOrLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                   bytes[0] == 0;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal ||
                   address.IsIPv6SiteLocal ||
                   address.IsIPv6Multicast ||
                   address.GetAddressBytes()[0] is 0xFC or 0xFD;
        }

        return true;
    }
}
