namespace CODConnect.PacketEngine;

public ref struct UdpFrame
{
    public ushort SourcePort;
    public ushort DestinationPort;
    public ReadOnlySpan<byte> Payload;
}

public static class UdpPacket
{
    public const int HeaderLength = 8;
    public const ushort PortDhcpServer = 67;
    public const ushort PortDhcpClient = 68;
    public const ushort PortSsdp = 1900;

    public static bool TryParse(ReadOnlySpan<byte> ipPayload, out UdpFrame udp)
    {
        udp = default;
        if (ipPayload.Length < HeaderLength)
        {
            return false;
        }

        udp.SourcePort = (ushort)((ipPayload[0] << 8) | ipPayload[1]);
        udp.DestinationPort = (ushort)((ipPayload[2] << 8) | ipPayload[3]);
        var length = (ipPayload[4] << 8) | ipPayload[5];
        if (length < HeaderLength || length > ipPayload.Length)
        {
            return false;
        }

        udp.Payload = ipPayload.Slice(HeaderLength, length - HeaderLength);
        return true;
    }

    public static byte[] BuildPayload(
        ushort sourcePort,
        ushort destinationPort,
        ReadOnlySpan<byte> data,
        IPv4Address sourceIp = default,
        IPv4Address destinationIp = default,
        bool computeChecksum = true)
    {
        var length = HeaderLength + data.Length;
        var payload = new byte[length];
        payload[0] = (byte)(sourcePort >> 8);
        payload[1] = (byte)sourcePort;
        payload[2] = (byte)(destinationPort >> 8);
        payload[3] = (byte)destinationPort;
        payload[4] = (byte)(length >> 8);
        payload[5] = (byte)length;
        payload[6] = 0;
        payload[7] = 0;
        data.CopyTo(payload.AsSpan(HeaderLength));

        if (!computeChecksum)
        {
            return payload;
        }

        Span<byte> pseudoHeader = stackalloc byte[12];
        sourceIp.WriteTo(pseudoHeader.Slice(0, 4));
        destinationIp.WriteTo(pseudoHeader.Slice(4, 4));
        pseudoHeader[8] = 0;
        pseudoHeader[9] = IPv4Packet.ProtocolUdp;
        pseudoHeader[10] = (byte)(length >> 8);
        pseudoHeader[11] = (byte)length;

        uint sum = 0;
        for (var i = 0; i + 1 < pseudoHeader.Length; i += 2)
        {
            sum += (uint)((pseudoHeader[i] << 8) | pseudoHeader[i + 1]);
        }

        for (var i = 0; i + 1 < payload.Length; i += 2)
        {
            sum += (uint)((payload[i] << 8) | payload[i + 1]);
        }

        if (payload.Length % 2 != 0)
        {
            sum += (uint)(payload[^1] << 8);
        }

        while (sum >> 16 != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        var checksum = (ushort)(~sum & 0xFFFF);
        if (checksum == 0)
        {
            checksum = 0xFFFF;
        }

        payload[6] = (byte)(checksum >> 8);
        payload[7] = (byte)checksum;
        return payload;
    }
}
