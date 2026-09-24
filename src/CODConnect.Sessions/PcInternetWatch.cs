namespace CODConnect.Sessions;

public sealed class PcInternetWatch(IPv4Address subnet, IPv4Address mask, TimeSpan patience, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    private DateTimeOffset? _firstAttempt;
    private bool _confirmed;

    public bool Confirmed
    {
        get
        {
            lock (_gate)
            {
                return _confirmed;
            }
        }
    }

    public bool GaveUp
    {
        get
        {
            lock (_gate)
            {
                return !_confirmed && _firstAttempt is { } first && _clock() - first >= patience;
            }
        }
    }

    public void ObserveConsoleFrame(ReadOnlySpan<byte> frame)
    {
        if (!EthernetFrame.TryParse(frame, out var ethernet)
            || ethernet.EtherType != EthernetFrame.EtherTypeIpv4
            || !IPv4Packet.TryParse(ethernet.Payload, out var ip)
            || ip.Protocol != IPv4Packet.ProtocolTcp
            || ip.Payload.Length < 14
            || !IsInternet(ip.Destination))
        {
            return;
        }

        const byte syn = 0x02, rst = 0x04, ack = 0x10;
        var flags = ip.Payload[13];
        lock (_gate)
        {
            if (_confirmed || (flags & rst) != 0)
            {
                return;
            }

            if ((flags & syn) != 0)
            {
                _firstAttempt ??= _clock();
            }
            else if ((flags & ack) != 0 && _firstAttempt is not null)
            {
                _confirmed = true;
            }
        }
    }

    private bool IsInternet(IPv4Address destination)
    {
        var value = destination.ToUInt32();
        var first = value >> 24;
        return (value & mask.ToUInt32()) != (subnet.ToUInt32() & mask.ToUInt32())
               && first is not (0 or 127) and < 224
               && !destination.IsBroadcast;
    }
}
