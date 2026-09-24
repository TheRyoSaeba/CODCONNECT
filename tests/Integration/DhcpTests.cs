using CODConnect.Core.Diagnostics;
using CODConnect.Core.Rooms;
using CODConnect.PacketEngine.Dhcp;

namespace CODConnect.IntegrationTests;

public class DhcpTests
{
    private static readonly IPv4Address ServerIp = new(10, 42, 0, 1);
    private static readonly MacAddress ServerMac = new(0x02, 0xCD, 0x00, 0x00, 0x00, 0x01);
    private static readonly MacAddress MacA = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x0A);
    private static readonly MacAddress MacB = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x0B);
    private static readonly MacAddress MacC = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x0C);

    private static byte[] DhcpRequestFrame(
        MacAddress clientMac,
        DhcpMessageType type,
        uint xid = 0x11223344,
        ushort flags = 0x0000,
        IPv4Address? requested = null,
        IPv4Address? serverId = null,
        string? hostname = null,
        IPv4Address? ciaddr = null)
    {
        var payload = DhcpPacket.BuildPayload(
            op: 1, messageType: type, xid: xid, flags: flags,
            ciaddr: ciaddr ?? IPv4Address.None, yiaddr: IPv4Address.None,
            siaddr: IPv4Address.None, giaddr: IPv4Address.None,
            clientMac: clientMac, hostname: hostname,
            requestedIp: requested, serverIdentifier: serverId,
            subnetMask: null, leaseSeconds: 0, broadcastAddress: null);
        var udp = UdpPacket.BuildPayload(UdpPacket.PortDhcpClient, UdpPacket.PortDhcpServer, payload, IPv4Address.None, IPv4Address.Broadcast, computeChecksum: false);
        var ipPacket = IPv4Packet.BuildPayload(IPv4Address.None, IPv4Address.Broadcast, IPv4Packet.ProtocolUdp, udp);
        return EthernetFrame.Build(MacAddress.Broadcast, clientMac, EthernetFrame.EtherTypeIpv4, ipPacket);
    }

    private static DhcpInfo ParseReply(byte[] frame)
    {
        Assert.True(EthernetFrame.TryParse(frame, out var parsed));
        Assert.Equal(EthernetFrame.EtherTypeIpv4, parsed.EtherType);
        Assert.True(IPv4Packet.TryParse(parsed.Payload, out var ip));
        Assert.True(UdpPacket.TryParse(ip.Payload, out var udp));
        Assert.Equal(UdpPacket.PortDhcpServer, udp.SourcePort);
        Assert.Equal(UdpPacket.PortDhcpClient, udp.DestinationPort);
        Assert.True(DhcpPacket.TryParse(udp.Payload, out var dhcp));
        return dhcp;
    }

    private static DhcpServer CreateServer(string? subnetCidr = null)
    {
        var options = subnetCidr is null
            ? new RoomOptions()
            : new RoomOptions { SubnetCidr = subnetCidr };
        return new DhcpServer(options);
    }

    [Fact]
    public void Discover_FromFreshMac_ProducesOfferWithExpectedAddressAndOptions()
    {
        var server = CreateServer();
        var replies = server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacA, DhcpMessageType.Discover, hostname: "PS5"), DateTimeOffset.UtcNow), ServerMac);

        var reply = Assert.Single(replies);
        var dhcp = ParseReply(reply);
        Assert.Equal(DhcpMessageType.Offer, dhcp.MessageType);
        Assert.Equal(new IPv4Address(10, 42, 0, 2), dhcp.Yiaddr);
        Assert.Equal(new IPv4Address(255, 255, 255, 0), dhcp.SubnetMask);
        Assert.Equal(ServerIp, dhcp.ServerIdentifier);
        Assert.Equal((uint)TimeSpan.FromHours(8).TotalSeconds, dhcp.LeaseSeconds);
        Assert.Equal(new IPv4Address(10, 42, 0, 255), dhcp.BroadcastAddress);
    }

    [Fact]
    public void Request_Accepted_ProducesAck_LeaseVisible()
    {
        var server = CreateServer();
        server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacA, DhcpMessageType.Discover), DateTimeOffset.UtcNow), ServerMac);

        var replies = server.HandleFrame(new CapturedFrame(
            DhcpRequestFrame(MacA, DhcpMessageType.Request, requested: new IPv4Address(10, 42, 0, 2), serverId: ServerIp),
            DateTimeOffset.UtcNow), ServerMac);

        var dhcp = ParseReply(Assert.Single(replies));
        Assert.Equal(DhcpMessageType.Ack, dhcp.MessageType);
        Assert.Equal(new IPv4Address(10, 42, 0, 2), dhcp.Yiaddr);
        Assert.True(server.Leases.TryGet(MacA, out var leased));
        Assert.Equal(new IPv4Address(10, 42, 0, 2), leased);
    }

    [Fact]
    public void Discover_SameMac_ReturnsStableAddress()
    {
        var server = CreateServer();
        server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacA, DhcpMessageType.Discover), DateTimeOffset.UtcNow), ServerMac);

        var replies = server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacA, DhcpMessageType.Discover), DateTimeOffset.UtcNow), ServerMac);

        var dhcp = ParseReply(Assert.Single(replies));
        Assert.Equal(new IPv4Address(10, 42, 0, 2), dhcp.Yiaddr);
    }

    [Fact]
    public void SecondMac_GetsDifferentAddress()
    {
        var server = CreateServer();
        server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacA, DhcpMessageType.Discover), DateTimeOffset.UtcNow), ServerMac);

        var replies = server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacB, DhcpMessageType.Discover), DateTimeOffset.UtcNow), ServerMac);

        var dhcp = ParseReply(Assert.Single(replies));
        Assert.Equal(new IPv4Address(10, 42, 0, 3), dhcp.Yiaddr);
    }

    [Fact]
    public void Request_ForAddressLeasedToOtherMac_ProducesNak()
    {
        var server = CreateServer();
        server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacA, DhcpMessageType.Discover), DateTimeOffset.UtcNow), ServerMac);

        var replies = server.HandleFrame(new CapturedFrame(
            DhcpRequestFrame(MacB, DhcpMessageType.Request, requested: new IPv4Address(10, 42, 0, 2), serverId: ServerIp),
            DateTimeOffset.UtcNow), ServerMac);

        var dhcp = ParseReply(Assert.Single(replies));
        Assert.Equal(DhcpMessageType.Nak, dhcp.MessageType);
    }

    [Fact]
    public void Request_ForeignServerIdentifier_IsIgnored()
    {
        var server = CreateServer();
        var replies = server.HandleFrame(new CapturedFrame(
            DhcpRequestFrame(MacA, DhcpMessageType.Request, requested: new IPv4Address(10, 42, 0, 2), serverId: new IPv4Address(10, 42, 0, 9)),
            DateTimeOffset.UtcNow), ServerMac);

        Assert.Empty(replies);
    }

    [Fact]
    public void Release_FreesAddress_NextDiscoverReusesLowest()
    {
        var server = CreateServer();
        server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacA, DhcpMessageType.Discover), DateTimeOffset.UtcNow), ServerMac);
        server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacB, DhcpMessageType.Discover), DateTimeOffset.UtcNow), ServerMac);

        server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacA, DhcpMessageType.Release, ciaddr: new IPv4Address(10, 42, 0, 2)), DateTimeOffset.UtcNow), ServerMac);
        Assert.False(server.Leases.TryGet(MacA, out _));

        var replies = server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacC, DhcpMessageType.Discover), DateTimeOffset.UtcNow), ServerMac);
        var dhcp = ParseReply(Assert.Single(replies));
        Assert.Equal(new IPv4Address(10, 42, 0, 2), dhcp.Yiaddr);
    }

    [Fact]
    public void BroadcastFlag_ControlsReplyDestinationMac()
    {
        var server = CreateServer();

        var broadcastReplies = server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacA, DhcpMessageType.Discover, flags: DhcpPacket.FlagBroadcast), DateTimeOffset.UtcNow), ServerMac);
        Assert.True(EthernetFrame.TryParse(Assert.Single(broadcastReplies), out var broadcastFrame));
        Assert.Equal(MacAddress.Broadcast, broadcastFrame.Destination);

        var unicastReplies = server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacB, DhcpMessageType.Discover), DateTimeOffset.UtcNow), ServerMac);
        Assert.True(EthernetFrame.TryParse(Assert.Single(unicastReplies), out var unicastFrame));
        Assert.Equal(MacB, unicastFrame.Destination);
    }

    [Fact]
    public void NonDhcpFrames_AreIgnored()
    {
        var counters = new NetworkCounters();
        var server = new DhcpServer(new RoomOptions(), counters);
        var arpFrame = ArpPacket.BuildEthernetFrame(MacA, new IPv4Address(10, 42, 0, 2), new IPv4Address(10, 42, 0, 3), isReply: false, replyDestinationMac: MacAddress.None);
        Assert.Empty(server.HandleFrame(new CapturedFrame(arpFrame, DateTimeOffset.UtcNow), ServerMac));

        var truncated = DhcpRequestFrame(MacA, DhcpMessageType.Discover)[..100];
        Assert.Empty(server.HandleFrame(new CapturedFrame(truncated, DateTimeOffset.UtcNow), ServerMac));
        Assert.Equal(1, counters.MalformedFrames);
    }

    [Fact]
    public void LeaseGranted_FiresWithHostname()
    {
        var server = CreateServer();
        MacAddress? grantedMac = null;
        string? grantedHostname = null;
        server.LeaseGranted += (mac, _, hostname) =>
        {
            grantedMac = mac;
            grantedHostname = hostname;
        };

        server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacA, DhcpMessageType.Discover, hostname: "PS4"), DateTimeOffset.UtcNow), ServerMac);

        Assert.Equal(MacA, grantedMac);
        Assert.Equal("PS4", grantedHostname);
    }

    [Fact]
    public void TinyPool_Exhaustion_ReturnsEmptyWithoutThrowing()
    {
        var server = CreateServer("10.42.0.0/30");
        var first = server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacA, DhcpMessageType.Discover), DateTimeOffset.UtcNow), ServerMac);
        var dhcp = ParseReply(Assert.Single(first));
        Assert.Equal(new IPv4Address(10, 42, 0, 2), dhcp.Yiaddr);

        var second = server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacB, DhcpMessageType.Discover), DateTimeOffset.UtcNow), ServerMac);
        Assert.Empty(second);
    }

    [Fact]
    public void Decline_MarksAddressUnavailable()
    {
        var server = CreateServer();
        server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacA, DhcpMessageType.Discover), DateTimeOffset.UtcNow), ServerMac);
        server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacA, DhcpMessageType.Decline, requested: new IPv4Address(10, 42, 0, 2)), DateTimeOffset.UtcNow), ServerMac);
        Assert.False(server.Leases.TryGet(MacA, out _));
        Assert.Equal(250, server.Pool.Available);
    }

    [Fact]
    public void Offer_And_Ack_NameTheGateway_WhichIsNeverLeased()
    {
        var server = CreateServer();
        var offer = ParseReply(Assert.Single(server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacA, DhcpMessageType.Discover), DateTimeOffset.UtcNow), ServerMac)));
        Assert.Equal(new IPv4Address(10, 42, 0, 254), offer.Router);
        Assert.Equal(new IPv4Address(1, 1, 1, 1), offer.DnsServer);

        var ack = ParseReply(Assert.Single(server.HandleFrame(new CapturedFrame(DhcpRequestFrame(MacA, DhcpMessageType.Request, requested: offer.Yiaddr, serverId: ServerIp), DateTimeOffset.UtcNow), ServerMac)));
        Assert.Equal(DhcpMessageType.Ack, ack.MessageType);
        Assert.Equal(new IPv4Address(10, 42, 0, 254), ack.Router);

        Assert.False(server.Pool.TryLeaseAddress(new IPv4Address(10, 42, 0, 254)));
    }

    [Fact]
    public void WindowsPc_IsNeverGivenARoomAddress()
    {
        var frame = DhcpRequestFrame(MacA, DhcpMessageType.Discover);
        var end = Array.LastIndexOf(frame, (byte)255);
        byte[] vendorClass = [60, 8, (byte)'M', (byte)'S', (byte)'F', (byte)'T', (byte)' ', (byte)'5', (byte)'.', (byte)'0'];
        var windows = frame[..end].Concat(vendorClass).Concat(frame[end..]).ToArray();
        FixLengths(windows, vendorClass.Length);
        Assert.True(EthernetFrame.TryParse(windows, out var eth) && IPv4Packet.TryParse(eth.Payload, out var ip)
            && UdpPacket.TryParse(ip.Payload, out var udp) && DhcpPacket.TryParse(udp.Payload, out var parsed) && parsed.VendorClass == "MSFT 5.0");

        Assert.Empty(CreateServer().HandleFrame(new CapturedFrame(windows, DateTimeOffset.UtcNow), ServerMac));
        Assert.NotEmpty(CreateServer().HandleFrame(new CapturedFrame(frame, DateTimeOffset.UtcNow), ServerMac));
    }

    private static void FixLengths(byte[] frame, int added)
    {
        var ipLength = ((frame[16] << 8) | frame[17]) + added;
        frame[16] = (byte)(ipLength >> 8); frame[17] = (byte)ipLength;
        var udpLength = ((frame[38] << 8) | frame[39]) + added;
        frame[38] = (byte)(udpLength >> 8); frame[39] = (byte)udpLength;
        frame[24] = 0; frame[25] = 0;
        uint sum = 0;
        for (var i = 14; i < 34; i += 2) sum += (uint)((frame[i] << 8) | frame[i + 1]);
        while (sum > 0xFFFF) sum = (sum & 0xFFFF) + (sum >> 16);
        var checksum = (ushort)~sum;
        frame[24] = (byte)(checksum >> 8); frame[25] = (byte)checksum;
    }
}
