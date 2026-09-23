using CODConnect.Core;
using CODConnect.Core.Diagnostics;

namespace CODConnect.PacketEngine;

public sealed class UserSpaceSwitch
{
    private sealed class PortEntry
    {
        public required string Name { get; init; }
        public required IConsoleNetworkInterface Interface { get; init; }
        public bool IsConsolePort { get; set; }

        public bool IsGatewayPort { get; set; }
        public MacAddress? ConsoleMac { get; set; }
        public bool ConsoleMacExplicit { get; set; }
    }

    private sealed class MacEntry
    {
        public int PortId { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    private readonly SwitchOptions _options;
    private readonly NetworkCounters? _counters;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<int, PortEntry> _ports = new();
    private readonly Dictionary<MacAddress, MacEntry> _macTable = new();
    private readonly object _lock = new();
    private int _nextPortId;

    public UserSpaceSwitch(SwitchOptions? options = null, NetworkCounters? counters = null, Func<DateTimeOffset>? clock = null)
    {
        _options = options ?? new SwitchOptions();
        _counters = counters;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event Action<int, CapturedFrame>? FrameForwarded;

    public event Action<int, CapturedFrame>? FrameAccepted;

    public int AddPort(string name, IConsoleNetworkInterface iface)
    {
        ArgumentNullException.ThrowIfNull(iface);
        lock (_lock)
        {
            var portId = ++_nextPortId;
            _ports[portId] = new PortEntry { Name = name, Interface = iface };
            return portId;
        }
    }

    public string PortName(int portId)
    {
        lock (_lock)
        {
            return _ports.TryGetValue(portId, out var port) ? port.Name : throw new KeyNotFoundException($"Unknown port {portId}.");
        }
    }

    public bool RemovePort(int portId)
    {
        lock (_lock)
        {
            foreach (var mac in _macTable.Where(kv => kv.Value.PortId == portId).Select(kv => kv.Key).ToList())
            {
                _macTable.Remove(mac);
            }

            return _ports.Remove(portId);
        }
    }

    public void MarkGatewayPort(int portId)
    {
        lock (_lock)
        {
            _ports[portId].IsGatewayPort = true;
        }
    }

    public void MarkConsolePort(int portId)
    {
        lock (_lock)
        {
            _ports[portId].IsConsolePort = true;
        }
    }

    public void RegisterConsoleMac(int portId, MacAddress mac)
    {
        lock (_lock)
        {
            var port = _ports[portId];
            port.IsConsolePort = true;
            port.ConsoleMac = mac;
            port.ConsoleMacExplicit = true;
        }
    }

    public void HandleFrame(int ingressPortId, CapturedFrame frame)
    {
        PortEntry ingress;
        List<int> egressPorts;
        lock (_lock)
        {
            if (!_ports.TryGetValue(ingressPortId, out ingress!))
            {
                _counters?.RecordDropped();
                return;
            }

            if (!EthernetFrame.TryParse(frame.Buffer.Span, out var parsed))
            {
                _counters?.RecordMalformed();
                return;
            }

            var broadcast = parsed.Destination.IsBroadcast || parsed.Destination.IsMulticast;
            _counters?.RecordCaptured(frame.Buffer.Length, broadcast);

            var learnableSource = !parsed.Source.IsNone && !parsed.Source.IsBroadcast && !parsed.Source.IsMulticast;
            if (learnableSource && _options.IsLocalSource(parsed.Source))
            {
                _counters?.RecordDropped();
                return;
            }

            if (!ingress.IsConsolePort && !ingress.IsGatewayPort && _options.TunnelIngressFilter?.Invoke(frame.Buffer) == false)
            {
                _counters?.RecordDropped();
                return;
            }

            if (learnableSource)
            {
                Learn(ingressPortId, parsed.Source);
            }

            if (ingress.IsConsolePort && learnableSource)
            {
                if (ingress.ConsoleMac is null)
                {
                    ingress.ConsoleMac = parsed.Source;
                }

                if (_options.StrictConsoleScoping && ingress.ConsoleMac != parsed.Source)
                {
                    _counters?.RecordDropped();
                    return;
                }
            }

            egressPorts = _options.LocalMacs.Contains(parsed.Destination)
                ? []
                : ComputeEgress(ingress, ingressPortId, parsed.Destination);
        }

        FrameAccepted?.Invoke(ingressPortId, frame);
        foreach (var portId in egressPorts)
        {
            FrameForwarded?.Invoke(portId, frame);
        }
    }

    public IReadOnlyDictionary<MacAddress, int> MacTableSnapshot()
    {
        lock (_lock)
        {
            return _macTable.ToDictionary(kv => kv.Key, kv => kv.Value.PortId);
        }
    }

    public void RemoveStaleEntries()
    {
        var cutoff = _clock() - _options.MacAgeingTime;
        lock (_lock)
        {
            var stale = _macTable.Where(kv => kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToList();
            foreach (var mac in stale)
            {
                _macTable.Remove(mac);
            }
        }
    }

    private List<int> ComputeEgress(PortEntry ingress, int ingressPortId, MacAddress destination)
    {
        if (destination.IsNone)
        {
            return [];
        }

        var candidates = _ports
            .Where(kv => kv.Key != ingressPortId
                         && (ingress.IsGatewayPort ? kv.Value.IsConsolePort : ingress.IsConsolePort || !kv.Value.IsGatewayPort))
            .Select(kv => kv.Key)
            .ToList();

        if (destination.IsBroadcast || destination.IsMulticast)
        {
            return candidates;
        }

        if (_macTable.TryGetValue(destination, out var entry))
        {
            return candidates.Contains(entry.PortId) ? [entry.PortId] : [];
        }

        return candidates;
    }

    private void Learn(int portId, MacAddress source)
    {
        var now = _clock();
        if (_macTable.TryGetValue(source, out var entry))
        {
            entry.PortId = portId;
            entry.LastSeen = now;
            return;
        }

        if (_macTable.Count >= _options.MaxLearnedMacs)
        {
            var oldest = _macTable.MinBy(kv => kv.Value.LastSeen).Key;
            _macTable.Remove(oldest);
        }

        _macTable[source] = new MacEntry { PortId = portId, LastSeen = now };
    }
}
