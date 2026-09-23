using System.Text;
using CODConnect.PacketEngine;

namespace CODConnect.PocTool;

public static class FrameFormatter
{
    public static string Format(ReadOnlySpan<byte> frame)
    {
        if (!EthernetFrame.TryParse(frame, out var parsed))
        {
            return $"{frame.Length,5}B  <unparseable>  {Hex(frame[..Math.Min(frame.Length, 24)])}";
        }

        var sb = new StringBuilder();
        sb.Append($"{parsed.Source} > {parsed.Destination}  eth=0x{parsed.EtherType:X4}  {frame.Length,5}B");
        if (parsed.EtherType == EthernetFrame.EtherTypeArp && ArpPacket.TryParse(parsed.Payload, out var arp))
        {
            sb.Append($"  ARP {(arp.Operation == ArpPacket.OperationRequest ? "request" : "reply")}"
                      + $"  who-has {arp.TargetIp} tell {arp.SenderIp}");
        }
        else if (parsed.EtherType == EthernetFrame.EtherTypeIpv4 && IPv4Packet.TryParse(parsed.Payload, out var ip))
        {
            sb.Append($"  IP {ip.Source} > {ip.Destination}  proto={ip.Protocol}");
            if (ip.Protocol == IPv4Packet.ProtocolUdp && UdpPacket.TryParse(ip.Payload, out var udp))
            {
                sb.Append($"  UDP {udp.SourcePort}>{udp.DestinationPort}  len={udp.Payload.Length}");
            }
        }

        return sb.ToString();
    }

    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("X2")).Append(' ');
        }

        return sb.ToString().TrimEnd();
    }
}
