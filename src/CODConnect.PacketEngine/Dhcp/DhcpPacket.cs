using System.Text;

namespace CODConnect.PacketEngine.Dhcp;

public enum DhcpMessageType : byte
{
    None = 0,
    Discover = 1,
    Offer = 2,
    Request = 3,
    Decline = 4,
    Ack = 5,
    Nak = 6,
    Release = 7,
}

public sealed record DhcpInfo
{
    public byte Op { get; init; }
    public uint Xid { get; init; }
    public ushort Flags { get; init; }
    public IPv4Address Ciaddr { get; init; }
    public IPv4Address Yiaddr { get; init; }
    public IPv4Address Siaddr { get; init; }
    public IPv4Address Giaddr { get; init; }
    public MacAddress ClientMac { get; init; }
    public DhcpMessageType MessageType { get; init; }
    public IPv4Address RequestedIp { get; init; }
    public IPv4Address ServerIdentifier { get; init; }
    public IPv4Address SubnetMask { get; init; }
    public IPv4Address BroadcastAddress { get; init; }
    public uint LeaseSeconds { get; init; }
    public string Hostname { get; init; } = string.Empty;
    public IPv4Address Router { get; init; }

    public string VendorClass { get; init; } = string.Empty;
    public IPv4Address DnsServer { get; init; }
}

public static class DhcpPacket
{
    public const int HeaderLength = 236;
    public const int MinPayloadLength = 240;
    public const ushort FlagBroadcast = 0x8000;

    public static bool TryParse(ReadOnlySpan<byte> payload, out DhcpInfo info)
    {
        info = new DhcpInfo();
        if (payload.Length < MinPayloadLength)
        {
            return false;
        }

        if (payload[236] != 0x63 || payload[237] != 0x82 || payload[238] != 0x53 || payload[239] != 0x63)
        {
            return false;
        }

        info = new DhcpInfo
        {
            Op = payload[0],
            Xid = (uint)((payload[4] << 24) | (payload[5] << 16) | (payload[6] << 8) | payload[7]),
            Flags = (ushort)((payload[10] << 8) | payload[11]),
            Ciaddr = new IPv4Address(payload.Slice(12, 4)),
            Yiaddr = new IPv4Address(payload.Slice(16, 4)),
            Siaddr = new IPv4Address(payload.Slice(20, 4)),
            Giaddr = new IPv4Address(payload.Slice(24, 4)),
            ClientMac = new MacAddress(payload.Slice(28, 6)),
            MessageType = DhcpMessageType.None,
            RequestedIp = IPv4Address.None,
            ServerIdentifier = IPv4Address.None,
            SubnetMask = IPv4Address.None,
            BroadcastAddress = IPv4Address.None,
            Hostname = string.Empty,
            Router = IPv4Address.None,
            DnsServer = IPv4Address.None,
        };

        var index = HeaderLength + 4;
        while (index < payload.Length)
        {
            var code = payload[index];
            if (code == 0)
            {
                index++;
                continue;
            }

            if (code == 255)
            {
                break;
            }

            if (index + 2 > payload.Length)
            {
                break;
            }

            var length = payload[index + 1];
            if (index + 2 + length > payload.Length)
            {
                break;
            }

            var value = payload.Slice(index + 2, length);
            switch (code)
            {
                case 53 when length >= 1:
                    info = info with { MessageType = (DhcpMessageType)value[0] };
                    break;
                case 50 when length == 4:
                    info = info with { RequestedIp = new IPv4Address(value) };
                    break;
                case 54 when length == 4:
                    info = info with { ServerIdentifier = new IPv4Address(value) };
                    break;
                case 1 when length == 4:
                    info = info with { SubnetMask = new IPv4Address(value) };
                    break;
                case 28 when length == 4:
                    info = info with { BroadcastAddress = new IPv4Address(value) };
                    break;
                case 51 when length == 4:
                    info = info with { LeaseSeconds = (uint)((value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3]) };
                    break;
                case 3 when length >= 4:
                    info = info with { Router = new IPv4Address(value[..4]) };
                    break;
                case 6 when length >= 4:
                    info = info with { DnsServer = new IPv4Address(value[..4]) };
                    break;
                case 60:
                    info = info with { VendorClass = Encoding.ASCII.GetString(value).TrimEnd('\0') };
                    break;
                case 12:
                    info = info with { Hostname = Encoding.ASCII.GetString(value).TrimEnd('\0') };
                    break;
            }

            index += 2 + length;
        }

        return true;
    }

    public static byte[] BuildPayload(
        byte op,
        DhcpMessageType messageType,
        uint xid,
        ushort flags,
        IPv4Address ciaddr,
        IPv4Address yiaddr,
        IPv4Address siaddr,
        IPv4Address giaddr,
        MacAddress clientMac,
        string? hostname,
        IPv4Address? requestedIp,
        IPv4Address? serverIdentifier,
        IPv4Address? subnetMask,
        uint leaseSeconds,
        IPv4Address? broadcastAddress,
        IPv4Address? router = null,
        IPv4Address? dnsServer = null)
    {
        var payload = new List<byte>(300);

        var header = new byte[HeaderLength];
        header[0] = op;
        header[1] = 1;
        header[2] = 6;
        header[3] = 0;
        PutUInt32(header.AsSpan(4), xid);
        header[8] = 0;
        header[9] = 0;
        header[10] = (byte)(flags >> 8);
        header[11] = (byte)flags;
        ciaddr.WriteTo(header.AsSpan(12, 4));
        yiaddr.WriteTo(header.AsSpan(16, 4));
        siaddr.WriteTo(header.AsSpan(20, 4));
        giaddr.WriteTo(header.AsSpan(24, 4));
        clientMac.WriteTo(header.AsSpan(28, 6));
        payload.AddRange(header);
        payload.AddRange([0x63, 0x82, 0x53, 0x63]);

        void AddOption(int code, params byte[] data)
        {
            payload.Add((byte)code);
            payload.Add((byte)data.Length);
            payload.AddRange(data);
        }

        if (messageType != DhcpMessageType.None)
        {
            AddOption(53, (byte)messageType);
        }

        if (requestedIp is { IsNone: false } requested)
        {
            AddOption(50, requested.ToArray());
        }

        if (serverIdentifier is { IsNone: false } server)
        {
            AddOption(54, server.ToArray());
        }

        if (!string.IsNullOrEmpty(hostname))
        {
            AddOption(12, Encoding.ASCII.GetBytes(hostname));
        }

        if (subnetMask is { IsNone: false } mask)
        {
            AddOption(1, mask.ToArray());
        }

        if (leaseSeconds > 0)
        {
            AddOption(51, [(byte)(leaseSeconds >> 24), (byte)(leaseSeconds >> 16), (byte)(leaseSeconds >> 8), (byte)leaseSeconds]);
        }

        if (broadcastAddress is { IsNone: false } broadcast)
        {
            AddOption(28, broadcast.ToArray());
        }

        if (router is { IsNone: false } gateway)
        {
            AddOption(3, gateway.ToArray());
        }

        if (dnsServer is { IsNone: false } dns)
        {
            AddOption(6, dns.ToArray());
        }

        payload.Add(255);
        while (payload.Count < 300)
        {
            payload.Add(0);
        }

        return payload.ToArray();
    }

    private static void PutUInt32(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }
}
