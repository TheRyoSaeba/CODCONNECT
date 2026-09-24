using CODConnect.Core.Rooms;
using CODConnect.PacketEngine.Dhcp;
using CODConnect.Sessions;
using CODConnect.NetworkSimulation;

namespace CODConnect.IntegrationTests;

public class RoomGuardTests
{
    private static readonly MacAddress Attacker = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x66);
    private static readonly RoomOptions Room = new();

    private static byte[] ArpClaim(IPv4Address claimed)
        => ArpPacket.BuildEthernetFrame(Attacker, claimed, new IPv4Address(10, 42, 0, 2), isReply: true, replyDestinationMac: new MacAddress(0x02, 0, 0, 0, 0, 0x02));

    private static byte[] DhcpOffer(IPv4Address? router, IPv4Address? dns, IPv4Address? serverId)
    {
        var payload = DhcpPacket.BuildPayload(
            op: 2, messageType: DhcpMessageType.Offer, xid: 1, flags: 0,
            ciaddr: IPv4Address.None, yiaddr: new IPv4Address(10, 42, 0, 3),
            siaddr: IPv4Address.None, giaddr: IPv4Address.None,
            clientMac: new MacAddress(0x02, 0, 0, 0, 0, 0x03), hostname: null,
            requestedIp: null, serverIdentifier: serverId,
            subnetMask: new IPv4Address(255, 255, 255, 0), leaseSeconds: 3600, broadcastAddress: null,
            router: router, dnsServers: dns is { } d ? [d] : null);
        var udp = UdpPacket.BuildPayload(UdpPacket.PortDhcpServer, UdpPacket.PortDhcpClient, payload, new IPv4Address(10, 42, 0, 1), IPv4Address.Broadcast);
        var ip = IPv4Packet.BuildPayload(new IPv4Address(10, 42, 0, 1), IPv4Address.Broadcast, IPv4Packet.ProtocolUdp, udp);
        return EthernetFrame.Build(MacAddress.Broadcast, Attacker, EthernetFrame.EtherTypeIpv4, ip);
    }

    [Fact]
    public void ClaimingTheGatewayOrThisPc_FromTheTunnel_IsDropped()
    {
        var guard = new RoomGuard(Room, isHost: false);
        Assert.False(guard.AllowsFromTunnel(ArpClaim(Room.GatewayAddress)));
        Assert.False(guard.AllowsFromTunnel(ArpClaim(Room.PcAddress)));
        Assert.True(guard.AllowsFromTunnel(ArpClaim(new IPv4Address(10, 42, 0, 3))));
    }

    [Fact]
    public void Joiner_AcceptsOnlyOffersNamingItsOwnGateway()
    {
        var guard = new RoomGuard(Room, isHost: false);
        Assert.True(guard.AllowsFromTunnel(DhcpOffer(Room.GatewayAddress, Room.GatewayAddress, Room.ServerAddress)));
        Assert.False(guard.AllowsFromTunnel(DhcpOffer(new IPv4Address(10, 42, 0, 77), Room.GatewayAddress, Room.ServerAddress)));
        Assert.True(guard.AllowsFromTunnel(DhcpOffer(Room.GatewayAddress, Room.DnsServers[0], Room.ServerAddress)));
        Assert.False(guard.AllowsFromTunnel(DhcpOffer(Room.GatewayAddress, new IPv4Address(9, 9, 9, 9), Room.ServerAddress)));
        Assert.False(guard.AllowsFromTunnel(DhcpOffer(Room.GatewayAddress, Room.GatewayAddress, new IPv4Address(10, 42, 0, 9))));
    }

    [Fact]
    public void Host_IgnoresEveryAddressServerButItsOwn()
        => Assert.False(new RoomGuard(Room, isHost: true).AllowsFromTunnel(DhcpOffer(Room.GatewayAddress, Room.GatewayAddress, Room.ServerAddress)));

    [Fact]
    public void Switch_DropsWhatTheGuardRefuses_OnlyFromTheTunnel()
    {
        var guard = new RoomGuard(Room, isHost: false);
        var sw = new UserSpaceSwitch(new SwitchOptions { TunnelIngressFilter = frame => guard.AllowsFromTunnel(frame.Span) });
        var forwarded = new List<int>();
        sw.FrameForwarded += (port, _) => forwarded.Add(port);
        var console = sw.AddPort("console", new RecordingPort("console", MacAddress.None));
        var tunnel = sw.AddPort("tunnel", new RecordingPort("tunnel", MacAddress.None));
        sw.MarkConsolePort(console);

        sw.HandleFrame(tunnel, new CapturedFrame(ArpClaim(Room.GatewayAddress), DateTimeOffset.UtcNow));
        Assert.Empty(forwarded);
    }
}
