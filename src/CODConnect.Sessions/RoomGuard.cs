using CODConnect.Core.Rooms;

namespace CODConnect.Sessions;

public sealed class RoomGuard(RoomOptions room, bool isHost, Func<bool>? friendsConsoleAccess = null)
{
    public static readonly IReadOnlySet<ushort> ConsoleAccessPorts = new HashSet<ushort> { 21, 22, 23, 80, 1337, 2121, 8080, 9020, 9021, 9090, 12800 };

    public bool AllowsFromTunnel(ReadOnlySpan<byte> frame)
    {
        if (!EthernetFrame.TryParse(frame, out var ethernet))
        {
            return true;
        }

        if (ethernet.EtherType == EthernetFrame.EtherTypeArp)
        {
            return !ArpPacket.TryParse(ethernet.Payload, out var arp) || !IsLocalOnly(arp.SenderIp);
        }

        if (ethernet.EtherType != EthernetFrame.EtherTypeIpv4 || !IPv4Packet.TryParse(ethernet.Payload, out var ip))
        {
            return true;
        }

        if (IsLocalOnly(ip.Source))
        {
            return false;
        }

        if (ip.Protocol == IPv4Packet.ProtocolTcp && ip.Payload.Length >= 14 && friendsConsoleAccess?.Invoke() != true)
        {
            var port = (ushort)((ip.Payload[2] << 8) | ip.Payload[3]);
            var flags = ip.Payload[13];
            if ((flags & 0x12) == 0x02 && ConsoleAccessPorts.Contains(port))
            {
                return false;
            }
        }

        if (ip.Protocol != IPv4Packet.ProtocolUdp
            || !UdpPacket.TryParse(ip.Payload, out var udp)
            || udp.SourcePort != UdpPacket.PortDhcpServer)
        {
            return true;
        }

        if (isHost || !DhcpPacket.TryParse(udp.Payload, out var dhcp))
        {
            return false;
        }

        return (dhcp.Router.IsNone || dhcp.Router == room.GatewayAddress)
               && (dhcp.DnsServer.IsNone || dhcp.DnsServer == room.GatewayAddress || room.DnsServers.Contains(dhcp.DnsServer))
               && (dhcp.ServerIdentifier.IsNone || dhcp.ServerIdentifier == room.ServerAddress);
    }

    private bool IsLocalOnly(IPv4Address address) => address == room.GatewayAddress || address == room.PcAddress;
}
