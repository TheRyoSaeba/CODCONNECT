using CODConnect.Core.Diagnostics;
using CODConnect.Core.Rooms;
using CODConnect.NetworkSimulation;
using CODConnect.PacketEngine.Dhcp;

namespace CODConnect.IntegrationTests;

public class EndToEndPipelineTests
{
    private static readonly MacAddress ConsoleAMac = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x0A);
    private static readonly MacAddress ConsoleBMac = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x0B);
    private static readonly MacAddress TunnelAMac = new(0x02, 0xCD, 0x00, 0x00, 0x00, 0x01);
    private static readonly MacAddress TunnelBMac = new(0x02, 0xCD, 0x00, 0x00, 0x00, 0x02);
    private static readonly IPv4Address ServerIp = new(10, 42, 0, 1);

    private sealed class Endpoint : IAsyncDisposable
    {
        public required VirtualPairLink ConsoleSim { get; init; }
        public required VirtualPairLink EnginePort { get; init; }
        public required TunnelPortAdapter TunnelPort { get; init; }
        public required SwitchEngine Engine { get; init; }
        public required UserSpaceSwitch Switch { get; init; }
        public DhcpServer? Dhcp { get; init; }
        public required NetworkCounters Counters { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await ConsoleSim.DisposeAsync();
            await EnginePort.DisposeAsync();
            await TunnelPort.DisposeAsync();
        }
    }

    private sealed record Pipeline(Endpoint Host, Endpoint Joiner, TcpTunnelTransport HostTunnel, TcpTunnelTransport JoinerTunnel) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Joiner.DisposeAsync();
            await Host.DisposeAsync();
            await JoinerTunnel.DisposeAsync();
            await HostTunnel.DisposeAsync();
        }
    }

    private static async Task<Pipeline> CreatePipelineAsync()
    {
        var hostTunnel = new TcpTunnelTransport(new TcpTunnelOptions { ListenPort = 0 });
        await hostTunnel.StartAsync();
        var joinerTunnel = new TcpTunnelTransport(new TcpTunnelOptions
        {
            ConnectHost = "127.0.0.1",
            ConnectPort = hostTunnel.BoundPort!.Value,
        });
        await joinerTunnel.StartAsync();

        var host = await CreateEndpointAsync("host", ConsoleAMac, hostTunnel, TunnelAMac, isRoomHost: true);
        var joiner = await CreateEndpointAsync("joiner", ConsoleBMac, joinerTunnel, TunnelBMac, isRoomHost: false);
        return new Pipeline(host, joiner, hostTunnel, joinerTunnel);
    }

    private static async Task<Endpoint> CreateEndpointAsync(string name, MacAddress consoleMac, ITunnelTransport tunnel, MacAddress tunnelMac, bool isRoomHost)
    {
        var (consoleSim, enginePort) = VirtualPairLink.CreatePair($"{name}-console", $"{name}-engine", consoleMac, MacAddress.None);
        var counters = new NetworkCounters();
        var sw = new UserSpaceSwitch(new SwitchOptions { StrictConsoleScoping = true }, counters);
        var engine = new SwitchEngine(sw);
        var tunnelPort = new TunnelPortAdapter(tunnel, $"{name}-tunnel", tunnelMac);
        engine.AddPort(enginePort, isConsolePort: true);
        engine.AddPort(tunnelPort, isConsolePort: false);

        DhcpServer? dhcp = null;
        if (isRoomHost)
        {
            dhcp = new DhcpServer(new RoomOptions(), counters);
            var ports = new List<IConsoleNetworkInterface> { enginePort, tunnelPort };
            sw.FrameForwarded += (_, frame) => TryServeDhcp(dhcp, frame, ports);
        }

        await engine.StartAsync();
        return new Endpoint
        {
            ConsoleSim = consoleSim,
            EnginePort = enginePort,
            TunnelPort = tunnelPort,
            Engine = engine,
            Switch = sw,
            Dhcp = dhcp,
            Counters = counters,
        };
    }

    private static void TryServeDhcp(DhcpServer dhcp, CapturedFrame frame, IReadOnlyList<IConsoleNetworkInterface> ports)
    {
        if (!EthernetFrame.TryParse(frame.Buffer.Span, out var parsed)
            || parsed.EtherType != EthernetFrame.EtherTypeIpv4
            || !IPv4Packet.TryParse(parsed.Payload, out var ip)
            || ip.Protocol != IPv4Packet.ProtocolUdp
            || !UdpPacket.TryParse(ip.Payload, out var udp)
            || udp.DestinationPort != UdpPacket.PortDhcpServer)
        {
            return;
        }

        foreach (var reply in dhcp.HandleFrame(frame, ports[0].GetMac()))
        {
            foreach (var port in ports)
            {
                port.InjectFrameAsync(reply).AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private static byte[] DhcpRequestFrame(MacAddress clientMac, DhcpMessageType type, ushort flags, IPv4Address? requested = null, string? hostname = null)
    {
        var payload = DhcpPacket.BuildPayload(
            op: 1, messageType: type, xid: 0x11223344, flags: flags,
            ciaddr: IPv4Address.None, yiaddr: IPv4Address.None,
            siaddr: IPv4Address.None, giaddr: IPv4Address.None,
            clientMac: clientMac, hostname: hostname,
            requestedIp: requested, serverIdentifier: ServerIp,
            subnetMask: null, leaseSeconds: 0, broadcastAddress: null);
        var udp = UdpPacket.BuildPayload(UdpPacket.PortDhcpClient, UdpPacket.PortDhcpServer, payload, IPv4Address.None, IPv4Address.Broadcast, computeChecksum: false);
        var ipPacket = IPv4Packet.BuildPayload(IPv4Address.None, IPv4Address.Broadcast, IPv4Packet.ProtocolUdp, udp);
        return EthernetFrame.Build(MacAddress.Broadcast, clientMac, EthernetFrame.EtherTypeIpv4, ipPacket);
    }

    private static async Task<IReadOnlyList<byte[]>> DrainUntilAsync(VirtualPairLink console, Func<List<byte[]>, bool> done, TimeSpan timeout)
    {
        var received = new List<byte[]>();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !done(received))
        {
            var frame = console.CaptureFrame();
            if (frame is not null)
            {
                received.Add(frame.Value.Buffer.ToArray());
                continue;
            }

            await Task.Delay(5);
        }

        return received;
    }

    [Fact]
    public async Task ArpBroadcast_CrossesTunnel_ReplyFindsItsWayBack()
    {
        await using var pipeline = await CreatePipelineAsync();

        var arpRequest = ArpPacket.BuildEthernetFrame(ConsoleBMac, new IPv4Address(10, 42, 0, 11), new IPv4Address(10, 42, 0, 12), isReply: false, replyDestinationMac: MacAddress.None);
        await pipeline.Joiner.ConsoleSim.InjectFrameAsync(arpRequest);

        var receivedOnA = await DrainUntilAsync(pipeline.Host.ConsoleSim, frames => frames.Count >= 1, TimeSpan.FromSeconds(10));
        var echoedRequest = Assert.Single(receivedOnA);
        Assert.True(echoedRequest.SequenceEqual(arpRequest));

        var arpReply = ArpPacket.BuildEthernetFrame(ConsoleAMac, new IPv4Address(10, 42, 0, 12), new IPv4Address(10, 42, 0, 11), isReply: true, replyDestinationMac: ConsoleBMac);
        await pipeline.Host.ConsoleSim.InjectFrameAsync(arpReply);

        var receivedOnB = await DrainUntilAsync(pipeline.Joiner.ConsoleSim, frames => frames.Count >= 1, TimeSpan.FromSeconds(10));
        var echoedReply = Assert.Single(receivedOnB);
        Assert.True(echoedReply.SequenceEqual(arpReply));

        Assert.Equal(0, pipeline.Host.Counters.FramesDropped);
        Assert.Equal(0, pipeline.Joiner.Counters.FramesDropped);
    }

    [Fact]
    public async Task DhcpDiscoverAndRequest_CrossTunnel_JoinerConsoleGetsLease()
    {
        await using var pipeline = await CreatePipelineAsync();

        var discover = DhcpRequestFrame(ConsoleBMac, DhcpMessageType.Discover, DhcpPacket.FlagBroadcast, hostname: "PS5");
        await pipeline.Joiner.ConsoleSim.InjectFrameAsync(discover);

        var afterDiscover = await DrainUntilAsync(
            pipeline.Joiner.ConsoleSim,
            frames => frames.Any(f => DescribeDhcp(f).StartsWith("Offer")),
            TimeSpan.FromSeconds(10));
        var offerDhcp = DescribeDhcp(Assert.Single(afterDiscover));
        Assert.Contains("yiaddr=10.42.0.2", offerDhcp);

        var request = DhcpRequestFrame(ConsoleBMac, DhcpMessageType.Request, DhcpPacket.FlagBroadcast, requested: new IPv4Address(10, 42, 0, 2));
        await pipeline.Joiner.ConsoleSim.InjectFrameAsync(request);

        var afterRequest = await DrainUntilAsync(pipeline.Joiner.ConsoleSim, frames => frames.Count >= 1, TimeSpan.FromSeconds(10));
        Assert.StartsWith("Ack", DescribeDhcp(afterRequest[0]));

        Assert.True(pipeline.Host.Dhcp!.Leases.TryGet(ConsoleBMac, out var leased));
        Assert.Equal(new IPv4Address(10, 42, 0, 2), leased);
        Assert.Equal(0, pipeline.Host.Counters.FramesDropped);
    }

    private static string DescribeDhcp(byte[] frame)
    {
        if (!EthernetFrame.TryParse(frame, out var parsed)
            || parsed.EtherType != EthernetFrame.EtherTypeIpv4
            || !IPv4Packet.TryParse(parsed.Payload, out var ip)
            || ip.Protocol != IPv4Packet.ProtocolUdp
            || !UdpPacket.TryParse(ip.Payload, out var udp)
            || !DhcpPacket.TryParse(udp.Payload, out var dhcp))
        {
            return "not-dhcp";
        }

        return $"{dhcp.MessageType} yiaddr={dhcp.Yiaddr}";
    }
}
