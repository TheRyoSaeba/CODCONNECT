using CODConnect.Discovery;

namespace CODConnect.Sessions;

public enum DeviceSide
{
    Local,

    Remote,
}

public sealed record ObservedDevice(
    MacAddress Mac,
    DeviceSide Side,
    IPv4Address? Ip,
    string? Hostname,
    string? Identity,
    DateTimeOffset LastSeenUtc);

public sealed class RoomDeviceTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<MacAddress, Entry> _devices = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<bool> _tunnelUp;
    private readonly Func<bool> _consoleLinkUp;
    private int _consolePortId;
    private MacAddress _dataPortMac;
    private int? _ignoredPortId;

    public RoomDeviceTracker(int consolePortId, MacAddress dataPortMac, Func<bool>? tunnelUp = null, Func<bool>? consoleLinkUp = null, Func<DateTimeOffset>? clock = null)
    {
        _consolePortId = consolePortId;
        _dataPortMac = dataPortMac;
        _tunnelUp = tunnelUp ?? (() => true);
        _consoleLinkUp = consoleLinkUp ?? (() => true);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(40);

    public void SetPorts(int consolePortId, MacAddress dataPortMac)
    {
        lock (_lock)
        {
            _consolePortId = consolePortId;
            _dataPortMac = dataPortMac;
        }
    }

    public void IgnorePort(int portId)
    {
        lock (_lock)
        {
            _ignoredPortId = portId;
        }
    }

    public void OnFrame(int ingressPortId, CapturedFrame frame)
    {
        if (!EthernetFrame.TryParse(frame.Buffer.Span, out var parsed))
        {
            return;
        }

        var source = parsed.Source;
        string? hostname = null;
        string? vendorClass = null;
        MacAddress? ackClient = null;
        IPv4Address? ackAddress = null;
        if (parsed.EtherType == EthernetFrame.EtherTypeIpv4
            && IPv4Packet.TryParse(parsed.Payload, out var ip)
            && ip.Protocol == IPv4Packet.ProtocolUdp
            && UdpPacket.TryParse(ip.Payload, out var udp)
            && DhcpPacket.TryParse(udp.Payload, out var dhcp))
        {
            if (udp.DestinationPort == UdpPacket.PortDhcpServer)
            {
                hostname = dhcp.Hostname.Length > 0 ? dhcp.Hostname : null;
                vendorClass = dhcp.VendorClass.Length > 0 ? dhcp.VendorClass : null;
            }
            else if (udp.DestinationPort == UdpPacket.PortDhcpClient
                     && dhcp.MessageType == DhcpMessageType.Ack
                     && !dhcp.Yiaddr.IsNone)
            {
                ackClient = dhcp.ClientMac;
                ackAddress = dhcp.Yiaddr;
            }
        }

        string? mdnsName = null;
        if (parsed.EtherType == EthernetFrame.EtherTypeIpv4
            && IPv4Packet.TryParse(parsed.Payload, out var mdnsIp)
            && mdnsIp.Protocol == IPv4Packet.ProtocolUdp
            && UdpPacket.TryParse(mdnsIp.Payload, out var mdnsUdp)
            && mdnsUdp.SourcePort == MdnsPacket.Port
            && MdnsPacket.TryGetOwnHostName(mdnsUdp.Payload, mdnsIp.Source, out var announced))
        {
            mdnsName = announced;
        }

        var now = _clock();
        lock (_lock)
        {
            if (ackClient is { } client && _devices.TryGetValue(client, out var target))
            {
                target.Ip = ackAddress;
            }

            if (source.IsNone || source.IsBroadcast || source.IsMulticast || source == _dataPortMac || ingressPortId == _ignoredPortId)
            {
                return;
            }

            var side = ingressPortId == _consolePortId ? DeviceSide.Local : DeviceSide.Remote;
            if (side == DeviceSide.Remote ? !_tunnelUp() : !_consoleLinkUp())
            {
                return;
            }

            var entry = Touch(source, side, now);
            if (hostname is not null)
            {
                entry.Hostname = hostname;
            }

            if (vendorClass is not null)
            {
                entry.VendorClass = vendorClass;
            }

            if (entry.Hostname is null && mdnsName is not null)
            {
                entry.Hostname = mdnsName;
            }
        }
    }

    public void OnLease(MacAddress mac, IPv4Address address, string hostname)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(mac, out var entry))
            {
                return;
            }

            entry.Ip = address;
            if (hostname.Length > 0)
            {
                entry.Hostname = hostname;
            }
        }
    }

    public IReadOnlyList<ObservedDevice> Snapshot()
    {
        var consoleLinkUp = _consoleLinkUp();
        var cutoff = _clock() - StaleAfter;
        lock (_lock)
        {
            return _devices
                .Where(kv => (kv.Value.Side == DeviceSide.Local && consoleLinkUp) || kv.Value.LastSeen >= cutoff)
                .Select(kv => new ObservedDevice(
                    kv.Key,
                    kv.Value.Side,
                    kv.Value.Ip,
                    kv.Value.Hostname,
                    ConsoleDetector.DetectConsoleType(kv.Key, kv.Value.Hostname, vendorClass: kv.Value.VendorClass),
                    kv.Value.LastSeen))
                .OrderBy(d => d.Side)
                .ThenByDescending(d => d.Identity is not null)
                .ThenByDescending(d => d.LastSeenUtc)
                .ToList();
        }
    }

    public bool IsSideReady(DeviceSide side)
        => (side == DeviceSide.Local ? _consoleLinkUp() : _tunnelUp()) && Snapshot().Any(d => d.Side == side);

    public void ClearSide(DeviceSide side)
    {
        lock (_lock)
        {
            foreach (var mac in _devices.Where(kv => kv.Value.Side == side).Select(kv => kv.Key).ToList())
            {
                _devices.Remove(mac);
            }
        }
    }

    private Entry Touch(MacAddress mac, DeviceSide side, DateTimeOffset now)
    {
        if (!_devices.TryGetValue(mac, out var entry))
        {
            entry = new Entry();
            _devices[mac] = entry;
        }

        entry.Side = side;
        entry.LastSeen = now;
        return entry;
    }

    private sealed class Entry
    {
        public DeviceSide Side { get; set; }
        public IPv4Address? Ip { get; set; }
        public string? Hostname { get; set; }
        public string? VendorClass { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }
}
