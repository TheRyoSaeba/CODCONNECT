using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CODConnect.Sessions;

public sealed class TcpRoomTransport : IRoomTransport
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public string ConnectionKind => "Direct";

    public int ListenPort { get; init; } = SessionDefaults.TunnelListenPort;

    private static MacAddress HostTunnelMac => new(0x02, 0xCD, 0x00, 0x00, 0x00, 0x01);

    private static MacAddress JoinerTunnelMac => new(0x02, 0xCD, 0x00, 0x00, 0x00, 0x02);

    public async Task<(IConsoleNetworkInterface Port, EndpointInfo Advertise)> HostAsync(CancellationToken cancellationToken = default)
    {
        var transport = new TcpTunnelTransport(new TcpTunnelOptions { ListenPort = ListenPort });
        await transport.StartAsync(cancellationToken).ConfigureAwait(false);
        var port = new TunnelPortAdapter(transport, "tcp-tunnel", HostTunnelMac);
        return (port, new EndpointInfo(AdvertiseAddresses(), transport.BoundPort!.Value));
    }

    public async Task<IConsoleNetworkInterface> JoinAsync(IReadOnlyList<EndpointInfo> peers, CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        foreach (var peer in peers)
        {
            foreach (var address in peer.Addresses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var transport = new TcpTunnelTransport(new TcpTunnelOptions
                {
                    ConnectHost = address,
                    ConnectPort = peer.Port,
                    ConnectTimeout = ConnectTimeout,
                });

                try
                {
                    await transport.StartAsync(cancellationToken).ConfigureAwait(false);
                    return new TunnelPortAdapter(transport, "tcp-tunnel", JoinerTunnelMac);
                }
                catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or InvalidOperationException)
                {
                    failures.Add($"{address}:{peer.Port} ({ex.Message})");
                    await transport.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException(
            "Could not reach any advertised peer endpoint. " + string.Join("; ", failures));
    }

    internal static IReadOnlyList<string> AdvertiseAddresses()
    {
        var routed = new List<string>();
        var all = new List<string>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up
                || networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var properties = networkInterface.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var address in properties.UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                var text = address.Address.ToString();
                if (!text.StartsWith("169.254.") && !text.StartsWith("10.42.0.") && !text.StartsWith("192.168.137."))
                {
                    all.Add(text);
                    if (hasGateway)
                    {
                        routed.Add(text);
                    }
                }
            }
        }

        return routed.Count > 0 ? routed : all;
    }
}
