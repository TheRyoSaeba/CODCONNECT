namespace CODConnect.PacketEngine;

public ref struct ParsedFrame
{
    public MacAddress Destination;
    public MacAddress Source;
    public ushort EtherType;
    public int VlanId;
    public ReadOnlySpan<byte> Payload;

    public readonly bool IsBroadcast => Destination.IsBroadcast;
    public readonly bool IsMulticast => Destination.IsMulticast;
}

public static class EthernetFrame
{
    public const int HeaderLength = 14;
    public const int MinFrameLength = 60;
    public const ushort EtherTypeIpv4 = 0x0800;
    public const ushort EtherTypeArp = 0x0806;
    public const ushort EtherTypeIpv6 = 0x86DD;
    public const ushort EtherTypeVlan = 0x8100;

    public static bool TryParse(ReadOnlySpan<byte> buffer, out ParsedFrame frame)
    {
        frame = default;
        if (buffer.Length < HeaderLength)
        {
            return false;
        }

        frame.Destination = new MacAddress(buffer.Slice(0, 6));
        frame.Source = new MacAddress(buffer.Slice(6, 6));
        var etherType = (ushort)((buffer[12] << 8) | buffer[13]);
        var payload = buffer.Slice(HeaderLength);
        frame.VlanId = -1;
        while (etherType == EtherTypeVlan && payload.Length >= 4)
        {
            frame.VlanId = ((payload[0] & 0x0F) << 8) | payload[1];
            etherType = (ushort)((payload[2] << 8) | payload[3]);
            payload = payload.Slice(4);
        }

        frame.EtherType = etherType;
        frame.Payload = payload;
        return true;
    }

    public static void WriteHeader(Span<byte> destination, MacAddress destinationMac, MacAddress sourceMac, ushort etherType)
    {
        if (destination.Length < HeaderLength)
        {
            throw new ArgumentException("Destination span must be at least 14 bytes.", nameof(destination));
        }

        destinationMac.WriteTo(destination.Slice(0, 6));
        sourceMac.WriteTo(destination.Slice(6, 6));
        destination[12] = (byte)(etherType >> 8);
        destination[13] = (byte)etherType;
    }

    public static byte[] Build(MacAddress destinationMac, MacAddress sourceMac, ushort etherType, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderLength + payload.Length];
        WriteHeader(frame, destinationMac, sourceMac, etherType);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }
}
