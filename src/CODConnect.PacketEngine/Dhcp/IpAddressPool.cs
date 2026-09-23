namespace CODConnect.PacketEngine.Dhcp;

public sealed class IpAddressPool
{
    private readonly uint _network;
    private readonly uint _broadcast;
    private readonly uint _server;
    private readonly List<uint> _free = new();
    private readonly object _lock = new();

    public IpAddressPool(IPv4Address serverAddress, IPv4Address subnetMask, IEnumerable<IPv4Address>? reserved = null)
    {
        var reservedRaw = (reserved ?? []).Where(a => !a.IsNone).Select(a => a.ToUInt32()).ToHashSet();
        var mask = subnetMask.ToUInt32();
        _server = serverAddress.ToUInt32();
        _network = _server & mask;
        _broadcast = _network | ~mask;

        for (var address = _network + 1; address < _broadcast; address++)
        {
            if (address != _server && !reservedRaw.Contains(address))
            {
                _free.Add(address);
            }
        }
    }

    public int Available
    {
        get
        {
            lock (_lock)
            {
                return _free.Count;
            }
        }
    }

    public bool TryLease(out IPv4Address address)
    {
        lock (_lock)
        {
            if (_free.Count == 0)
            {
                address = IPv4Address.None;
                return false;
            }

            var lowest = _free.Min();
            _free.Remove(lowest);
            address = IPv4Address.FromUInt32(lowest);
            return true;
        }
    }

    public bool TryLeaseAddress(IPv4Address address)
    {
        lock (_lock)
        {
            return _free.Remove(address.ToUInt32());
        }
    }

    public bool TryRelease(IPv4Address address)
    {
        lock (_lock)
        {
            var raw = address.ToUInt32();
            if (!Contains(raw) || _free.Contains(raw))
            {
                return false;
            }

            _free.Add(raw);
            return true;
        }
    }

    public bool TryMarkUnavailable(IPv4Address address)
    {
        lock (_lock)
        {
            return _free.Remove(address.ToUInt32());
        }
    }

    public bool Contains(IPv4Address address)
    {
        lock (_lock)
        {
            return Contains(address.ToUInt32());
        }
    }

    private bool Contains(uint raw)
        => raw > _network && raw < _broadcast && raw != _server;
}
