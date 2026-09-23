namespace CODConnect.PacketEngine;

public ref struct IPv4Frame
{
    public byte TimeToLive;
    public byte Protocol;
    public bool DontFragment;
    public IPv4Address Source;
    public IPv4Address Destination;
    public ReadOnlySpan<byte> Payload;
}

public static class IPv4Packet
{
    public const byte ProtocolIcmp = 1;
    public const byte ProtocolTcp = 6;
    public const byte ProtocolUdp = 17;
    public const int HeaderLength = 20;

    public static bool TryParse(ReadOnlySpan<byte> payload, out IPv4Frame ip)
    {
        ip = default;
        if (payload.Length < HeaderLength)
        {
            return false;
        }

        if (payload[0] >> 4 != 4)
        {
            return false;
        }

        var headerLength = (payload[0] & 0x0F) * 4;
        if (headerLength < HeaderLength || payload.Length < headerLength)
        {
            return false;
        }

        var totalLength = (payload[2] << 8) | payload[3];
        if (totalLength < headerLength || totalLength > payload.Length)
        {
            return false;
        }

        ip.TimeToLive = payload[8];
        ip.Protocol = payload[9];
        ip.DontFragment = (payload[6] & 0x40) != 0;
        ip.Source = new IPv4Address(payload.Slice(12, 4));
        ip.Destination = new IPv4Address(payload.Slice(16, 4));
        ip.Payload = payload.Slice(headerLength, totalLength - headerLength);
        return true;
    }

    public static byte[] BuildPayload(
        IPv4Address source,
        IPv4Address destination,
        byte protocol,
        ReadOnlySpan<byte> data,
        byte timeToLive = 64,
        ushort identification = 0,
        bool dontFragment = true)
    {
        var totalLength = HeaderLength + data.Length;
        var payload = new byte[totalLength];
        payload[0] = 0x45;
        payload[1] = 0;
        payload[2] = (byte)(totalLength >> 8);
        payload[3] = (byte)totalLength;
        payload[4] = (byte)(identification >> 8);
        payload[5] = (byte)identification;
        payload[6] = dontFragment ? (byte)0x40 : (byte)0x00;
        payload[7] = 0;
        payload[8] = timeToLive;
        payload[9] = protocol;
        source.WriteTo(payload.AsSpan(12, 4));
        destination.WriteTo(payload.AsSpan(16, 4));
        data.CopyTo(payload.AsSpan(HeaderLength));
        var checksum = ComputeChecksum(payload.AsSpan(0, HeaderLength));
        payload[10] = (byte)(checksum >> 8);
        payload[11] = (byte)checksum;
        return payload;
    }

    public static ushort ComputeChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 1 < data.Length; i += 2)
        {
            sum += (uint)((data[i] << 8) | data[i + 1]);
        }

        if (data.Length % 2 != 0)
        {
            sum += (uint)(data[^1] << 8);
        }

        while (sum >> 16 != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)(~sum & 0xFFFF);
    }
}
