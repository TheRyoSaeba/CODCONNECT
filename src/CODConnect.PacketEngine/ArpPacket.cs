namespace CODConnect.PacketEngine;

public ref struct ArpFrame
{
    public ushort Operation;
    public MacAddress SenderMac;
    public IPv4Address SenderIp;
    public MacAddress TargetMac;
    public IPv4Address TargetIp;
}

public static class ArpPacket
{
    public const int PayloadLength = 28;
    public const ushort OperationRequest = 1;
    public const ushort OperationReply = 2;

    public static bool TryParse(ReadOnlySpan<byte> payload, out ArpFrame arp)
    {
        arp = default;
        if (payload.Length < PayloadLength)
        {
            return false;
        }

        if (payload[0] != 0 || payload[1] != 1 || payload[2] != 8 || payload[3] != 0 || payload[4] != 6 || payload[5] != 4)
        {
            return false;
        }

        arp.Operation = (ushort)((payload[6] << 8) | payload[7]);
        arp.SenderMac = new MacAddress(payload.Slice(8, 6));
        arp.SenderIp = new IPv4Address(payload.Slice(14, 4));
        arp.TargetMac = new MacAddress(payload.Slice(18, 6));
        arp.TargetIp = new IPv4Address(payload.Slice(24, 4));
        return true;
    }

    public static byte[] BuildPayload(ushort operation, MacAddress senderMac, IPv4Address senderIp, MacAddress targetMac, IPv4Address targetIp)
    {
        var payload = new byte[PayloadLength];
        payload[0] = 0;
        payload[1] = 1;
        payload[2] = 8;
        payload[3] = 0;
        payload[4] = 6;
        payload[5] = 4;
        payload[6] = (byte)(operation >> 8);
        payload[7] = (byte)operation;
        senderMac.WriteTo(payload.AsSpan(8, 6));
        senderIp.WriteTo(payload.AsSpan(14, 4));
        targetMac.WriteTo(payload.AsSpan(18, 6));
        targetIp.WriteTo(payload.AsSpan(24, 4));
        return payload;
    }

    public static byte[] BuildEthernetFrame(MacAddress sourceMac, IPv4Address senderIp, IPv4Address targetIp, bool isReply, MacAddress replyDestinationMac)
    {
        var destinationMac = isReply ? replyDestinationMac : MacAddress.Broadcast;
        var targetMac = isReply ? replyDestinationMac : MacAddress.None;
        var payload = BuildPayload(isReply ? OperationReply : OperationRequest, sourceMac, senderIp, targetMac, targetIp);
        return EthernetFrame.Build(destinationMac, sourceMac, EthernetFrame.EtherTypeArp, payload);
    }
}
