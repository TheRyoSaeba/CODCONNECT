using CODConnect.Core;
using CODConnect.Core.Diagnostics;
using CODConnect.Core.Rooms;

namespace CODConnect.PacketEngine.Dhcp;

public sealed class DhcpServer
{
    private readonly RoomOptions _options;
    private readonly IPv4Address _subnetMask;
    private readonly IPv4Address _broadcastAddress;
    private readonly NetworkCounters? _counters;

    public DhcpServer(RoomOptions options, NetworkCounters? counters = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _counters = counters;
        (_subnetMask, _broadcastAddress) = ParseCidr(options.SubnetCidr);
        Pool = new IpAddressPool(options.ServerAddress, _subnetMask, [options.GatewayAddress, options.PcAddress]);
        Leases = new LeaseTable(Pool, options.LeaseDuration);
        Leases.LeaseGranted += (mac, address, hostname) => LeaseGranted?.Invoke(mac, address, hostname);
    }

    public IpAddressPool Pool { get; }

    public LeaseTable Leases { get; }

    public event Action<MacAddress, IPv4Address, string>? LeaseGranted;

    public IReadOnlyList<byte[]> HandleFrame(CapturedFrame frame, MacAddress serverMac)
    {
        if (!EthernetFrame.TryParse(frame.Buffer.Span, out var parsed))
        {
            _counters?.RecordMalformed();
            return [];
        }

        if (parsed.EtherType != EthernetFrame.EtherTypeIpv4)
        {
            return [];
        }

        if (!IPv4Packet.TryParse(parsed.Payload, out var ip)
            || !UdpPacket.TryParse(ip.Payload, out var udp))
        {
            _counters?.RecordMalformed();
            return [];
        }

        if (udp.DestinationPort != UdpPacket.PortDhcpServer)
        {
            return [];
        }

        if (!DhcpPacket.TryParse(udp.Payload, out var dhcp)
            || dhcp.MessageType is DhcpMessageType.None or DhcpMessageType.Offer or DhcpMessageType.Ack or DhcpMessageType.Nak)
        {
            _counters?.RecordMalformed();
            return [];
        }

        // Only consoles belong on the room's network. A Windows PC asks too when SoftEther turns
        // its adapter's networking back on, and a lease naming the room's gateway would send
        // that PC's own Internet traffic into the room.
        if (dhcp.VendorClass.StartsWith("MSFT", StringComparison.Ordinal))
        {
            return [];
        }

        return dhcp.MessageType switch
        {
            DhcpMessageType.Discover => HandleDiscover(dhcp, serverMac),
            DhcpMessageType.Request => HandleRequest(dhcp, serverMac),
            DhcpMessageType.Decline => HandleDecline(dhcp),
            DhcpMessageType.Release => HandleRelease(dhcp),
            _ => [],
        };
    }

    private IReadOnlyList<byte[]> HandleDiscover(DhcpInfo dhcp, MacAddress serverMac)
    {
        IPv4Address address;
        try
        {
            address = Leases.GetOrAssign(dhcp.ClientMac, dhcp.Hostname);
        }
        catch (InvalidOperationException)
        {
            _counters?.RecordDropped();
            return [];
        }

        return [BuildReply(DhcpMessageType.Offer, dhcp, address, serverMac)];
    }

    private IReadOnlyList<byte[]> HandleRequest(DhcpInfo dhcp, MacAddress serverMac)
    {
        if (!dhcp.ServerIdentifier.IsNone && dhcp.ServerIdentifier != _options.ServerAddress)
        {
            return [];
        }

        var requested = !dhcp.RequestedIp.IsNone ? dhcp.RequestedIp : dhcp.Ciaddr;
        if (requested.IsNone)
        {
            return [BuildReply(DhcpMessageType.Nak, dhcp, IPv4Address.None, serverMac)];
        }

        if (Leases.Assign(dhcp.ClientMac, requested, dhcp.Hostname))
        {
            return [BuildReply(DhcpMessageType.Ack, dhcp, requested, serverMac)];
        }

        return [BuildReply(DhcpMessageType.Nak, dhcp, IPv4Address.None, serverMac)];
    }

    private IReadOnlyList<byte[]> HandleDecline(DhcpInfo dhcp)
    {
        if (Leases.TryGet(dhcp.ClientMac, out var leased))
        {
            Leases.Release(dhcp.ClientMac);
            Pool.TryMarkUnavailable(leased);
        }

        if (!dhcp.RequestedIp.IsNone)
        {
            Pool.TryMarkUnavailable(dhcp.RequestedIp);
        }

        return [];
    }

    private IReadOnlyList<byte[]> HandleRelease(DhcpInfo dhcp)
    {
        if (Leases.TryGet(dhcp.ClientMac, out var leased)
            && (dhcp.Ciaddr.IsNone || dhcp.Ciaddr == leased))
        {
            Leases.Release(dhcp.ClientMac);
        }

        return [];
    }

    private byte[] BuildReply(DhcpMessageType type, DhcpInfo dhcp, IPv4Address yiaddr, MacAddress serverMac)
    {
        var isNak = type == DhcpMessageType.Nak;
        var flags = (ushort)(isNak ? dhcp.Flags | DhcpPacket.FlagBroadcast : dhcp.Flags);
        var payload = DhcpPacket.BuildPayload(
            op: 2,
            messageType: type,
            xid: dhcp.Xid,
            flags: flags,
            ciaddr: type == DhcpMessageType.Ack ? dhcp.Ciaddr : IPv4Address.None,
            yiaddr: yiaddr,
            siaddr: _options.ServerAddress,
            giaddr: IPv4Address.None,
            clientMac: dhcp.ClientMac,
            hostname: null,
            requestedIp: null,
            serverIdentifier: _options.ServerAddress,
            subnetMask: isNak ? null : _subnetMask,
            leaseSeconds: isNak ? 0 : (uint)_options.LeaseDuration.TotalSeconds,
            broadcastAddress: isNak ? null : _broadcastAddress,
            router: isNak ? null : _options.GatewayAddress,
            dnsServers: isNak ? null : _options.DnsServers);

        var destinationMac = isNak || (dhcp.Flags & DhcpPacket.FlagBroadcast) != 0 || dhcp.ClientMac.IsNone
            ? MacAddress.Broadcast
            : dhcp.ClientMac;
        var udp = UdpPacket.BuildPayload(
            UdpPacket.PortDhcpServer, UdpPacket.PortDhcpClient, payload,
            _options.ServerAddress, IPv4Address.Broadcast);
        var ipPacket = IPv4Packet.BuildPayload(_options.ServerAddress, IPv4Address.Broadcast, IPv4Packet.ProtocolUdp, udp);
        return EthernetFrame.Build(destinationMac, serverMac, EthernetFrame.EtherTypeIpv4, ipPacket);
    }

    private static (IPv4Address Mask, IPv4Address Broadcast) ParseCidr(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2
            || !IPv4Address.TryParse(parts[0], out var network)
            || !int.TryParse(parts[1], out var prefix)
            || prefix is < 0 or > 32)
        {
            throw new ArgumentException($"Invalid SubnetCidr '{cidr}'.", nameof(cidr));
        }

        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var networkRaw = network.ToUInt32() & mask;
        return (IPv4Address.FromUInt32(mask), IPv4Address.FromUInt32(networkRaw | ~mask));
    }
}
