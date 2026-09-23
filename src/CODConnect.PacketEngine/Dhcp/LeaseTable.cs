namespace CODConnect.PacketEngine.Dhcp;

public sealed class LeaseTable
{
    private sealed record Lease(IPv4Address Ip, DateTimeOffset Expires, string Hostname);

    private readonly IpAddressPool _pool;
    private readonly TimeSpan _leaseDuration;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<MacAddress, Lease> _leases = new();
    private readonly object _lock = new();

    public LeaseTable(IpAddressPool pool, TimeSpan leaseDuration, Func<DateTimeOffset>? clock = null)
    {
        _pool = pool;
        _leaseDuration = leaseDuration;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event Action<MacAddress, IPv4Address, string>? LeaseGranted;

    public IPv4Address GetOrAssign(MacAddress mac, string hostname = "")
    {
        lock (_lock)
        {
            SweepExpired();
            if (_leases.TryGetValue(mac, out var existing))
            {
                _leases[mac] = existing with { Expires = _clock() + _leaseDuration };
                return existing.Ip;
            }

            if (!_pool.TryLease(out var address))
            {
                throw new InvalidOperationException("Address pool exhausted.");
            }

            return Grant(mac, address, hostname);
        }
    }

    public bool Assign(MacAddress mac, IPv4Address address, string hostname = "")
    {
        lock (_lock)
        {
            SweepExpired();
            if (_leases.Any(kv => kv.Value.Ip == address && kv.Key != mac))
            {
                return false;
            }

            if (_leases.TryGetValue(mac, out var existing) && existing.Ip == address)
            {
                _leases[mac] = existing with { Expires = _clock() + _leaseDuration };
                return true;
            }

            if (_leases.TryGetValue(mac, out var previous))
            {
                _pool.TryRelease(previous.Ip);
                _leases.Remove(mac);
            }

            if (!_pool.TryLeaseAddress(address))
            {
                return false;
            }

            Grant(mac, address, hostname);
            return true;
        }
    }

    public bool TryGet(MacAddress mac, out IPv4Address address)
    {
        lock (_lock)
        {
            SweepExpired();
            if (_leases.TryGetValue(mac, out var lease))
            {
                address = lease.Ip;
                return true;
            }

            address = IPv4Address.None;
            return false;
        }
    }

    public void Release(MacAddress mac)
    {
        lock (_lock)
        {
            if (_leases.Remove(mac, out var lease))
            {
                _pool.TryRelease(lease.Ip);
            }
        }
    }

    public IReadOnlyDictionary<MacAddress, IPv4Address> Snapshot()
    {
        lock (_lock)
        {
            SweepExpired();
            return _leases.ToDictionary(kv => kv.Key, kv => kv.Value.Ip);
        }
    }

    public IReadOnlyList<(MacAddress Mac, IPv4Address Ip, string Hostname)> SnapshotWithHostnames()
    {
        lock (_lock)
        {
            SweepExpired();
            return _leases.Select(kv => (kv.Key, kv.Value.Ip, kv.Value.Hostname)).ToList();
        }
    }

    private IPv4Address Grant(MacAddress mac, IPv4Address address, string hostname)
    {
        var lease = new Lease(address, _clock() + _leaseDuration, hostname);
        _leases[mac] = lease;
        LeaseGranted?.Invoke(mac, address, hostname);
        return address;
    }

    private void SweepExpired()
    {
        var now = _clock();
        foreach (var (mac, lease) in _leases.Where(kv => kv.Value.Expires <= now).ToList())
        {
            _leases.Remove(mac);
            _pool.TryRelease(lease.Ip);
        }
    }
}
