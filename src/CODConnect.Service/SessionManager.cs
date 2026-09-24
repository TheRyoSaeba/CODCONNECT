namespace CODConnect.Service;

public sealed class SessionManager : IAsyncDisposable
{
    public RoomChatSnapshot? Chat { get { lock (_lock) return _session?.Chat?.Snapshot(); } }
    public void SendChat(string message)
    {
        lock (_lock)
        {
            if (_session?.Chat is not { } chat) throw new InvalidOperationException("Join a room to chat.");
            chat.Send(message);
        }
    }
    private readonly Func<string?, bool, Task<IConsoleNetworkInterface>> _consoleFactory;
    private readonly IRoomTransport _transport;
    private readonly HttpClient _rendezvous;
    private readonly DevSettings? _dev;

    public DevSettings? Dev => _dev;
    private readonly Action<string>? _log;
    private readonly object _lock = new();
    private RoomSession? _session;
    private RoomRole? _role;

    public string? LastEndReason { get; private set; }

    private readonly SessionJournal? _journal;
    private readonly Wifi.WifiRoomController? _wifi;
    private volatile string? _progress;

    public string? Progress => _progress;
    private CODConnect.Wifi.WifiHotspot? _wifiNetwork;

    public SessionManager(
        Func<string?, bool, Task<IConsoleNetworkInterface>> consoleFactory,
        IRoomTransport transport,
        HttpClient rendezvous,
        Action<string>? log = null,
        SessionJournal? journal = null,
        Wifi.WifiRoomController? wifi = null,
        DevSettings? dev = null)
    {
        _dev = dev;
        _consoleFactory = consoleFactory;
        _transport = transport;
        _rendezvous = rendezvous;
        _log = log;
        _journal = journal;
        _wifi = wifi;
    }

    public IpcWifiNetwork? WifiNetwork
    {
        get
        {
            lock (_lock)
            {
                return _session is not null && _wifiNetwork is { } n
                    ? new IpcWifiNetwork(n.Settings.Ssid, n.Settings.Passphrase, n.Settings.Band)
                    : null;
            }
        }
    }

    public async Task<IpcWifiCapability> CheckWifiAsync(CancellationToken cancellationToken = default)
    {
        if (_wifi is null)
        {
            return new IpcWifiCapability(false, "Wi-Fi mode is not available in this build.", null, null, null, []);
        }

        var report = await _wifi.CheckCapabilityAsync(cancellationToken).ConfigureAwait(false);
        return new IpcWifiCapability(report.Supported, report.Reason, report.AdapterDescription, report.Band, report.Channel, report.Warnings ?? []);
    }

    public bool HasSession
    {
        get
        {
            lock (_lock)
            {
                return _session is not null;
            }
        }
    }

    public string? RoomCode
    {
        get
        {
            lock (_lock)
            {
                return _session?.RoomCode;
            }
        }
    }

    public string? Role
    {
        get
        {
            lock (_lock)
            {
                return _role?.ToString();
            }
        }
    }

    public string? TunnelState
    {
        get
        {
            lock (_lock)
            {
                return _session?.TunnelState.ToString();
            }
        }
    }

    public NetworkCounterSnapshot? Counters
    {
        get
        {
            lock (_lock)
            {
                return _session?.Counters.Snapshot();
            }
        }
    }

    public IReadOnlyList<IpcLease> Leases
    {
        get
        {
            lock (_lock)
            {
                if (_session?.Dhcp is not { } dhcp)
                {
                    return [];
                }

                return dhcp.Leases.SnapshotWithHostnames()
                    .Select(l => new IpcLease(l.Mac.ToString(), l.Ip.ToString(), l.Hostname.Length == 0 ? null : l.Hostname))
                    .ToList();
            }
        }
    }

    public IReadOnlyList<IpcDevice> Devices
    {
        get
        {
            lock (_lock)
            {
                if (_session is null)
                {
                    return [];
                }

                return _session.Devices
                    .Select(d => new IpcDevice(d.Mac.ToString(), d.Side.ToString(), d.Ip?.ToString(), d.Hostname, d.Identity, d.LastSeenUtc))
                    .ToList();
            }
        }
    }

    public IReadOnlyList<IpcPlayer> Players
    {
        get
        {
            lock (_lock)
            {
                return _session?.Players ?? [];
            }
        }
    }

    public string? ConsoleInternet
    {
        get
        {
            lock (_lock)
            {
                return _session?.ConsoleInternet;
            }
        }
    }

    public bool LocalConsoleReady
    {
        get
        {
            lock (_lock)
            {
                return _session?.LocalConsoleReady == true;
            }
        }
    }

    public bool RemoteConsoleReady
    {
        get
        {
            lock (_lock)
            {
                return _session?.RemoteConsoleReady == true;
            }
        }
    }

    public string? ActiveTransport => _transport switch
    {
        AutoRoomTransport auto => auto.ActiveTransport,
        SoftEtherRoomTransport => "SoftEther",
        TcpRoomTransport => "TCP",
        _ => _transport.GetType().Name,
    };

    public string? ConnectionKind
    {
        get
        {
            lock (_lock)
            {
                return _session?.TunnelState == CODConnect.Networking.TunnelState.Connected ? _session.ConnectionKind : null;
            }
        }
    }

    public async Task<string> StartHostAsync(string? displayName, string? adapter, CancellationToken cancellationToken = default)
        => await StartHostAsync(displayName, adapter, allowInternetAdapter: false, cancellationToken).ConfigureAwait(false);

    public async Task<string> StartHostAsync(string? displayName, string? adapter, bool allowInternetAdapter, CancellationToken cancellationToken = default)
        => await StartHostAsync(displayName, adapter, allowInternetAdapter, wifi: false, cancellationToken).ConfigureAwait(false);

    public async Task<string> StartHostAsync(string? displayName, string? adapter, bool allowInternetAdapter, bool wifi, CancellationToken cancellationToken = default)
    {
        var session = await StartAsync(
            roomCode: null,
            (options, token) => RoomSession.HostAsync(options, token),
            displayName, adapter, allowInternetAdapter, wifi, cancellationToken).ConfigureAwait(false);
        _log?.Invoke($"hosting room {session.RoomCode}");
        return session.RoomCode;
    }

    public async Task StartJoinAsync(string roomCode, string? displayName, string? adapter, CancellationToken cancellationToken = default)
        => await StartJoinAsync(roomCode, displayName, adapter, allowInternetAdapter: false, cancellationToken).ConfigureAwait(false);

    public async Task StartJoinAsync(string roomCode, string? displayName, string? adapter, bool allowInternetAdapter, CancellationToken cancellationToken = default)
        => await StartJoinAsync(roomCode, displayName, adapter, allowInternetAdapter, wifi: false, cancellationToken).ConfigureAwait(false);

    public async Task StartJoinAsync(string roomCode, string? displayName, string? adapter, bool allowInternetAdapter, bool wifi, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roomCode))
        {
            throw new ArgumentException("A room code is required to join.", nameof(roomCode));
        }

        await StartAsync(
            roomCode,
            (options, token) => RoomSession.JoinAsync(options, token),
            displayName, adapter, allowInternetAdapter, wifi, cancellationToken).ConfigureAwait(false);
        _log?.Invoke($"joined room {roomCode}");
    }

    private async Task EndAsync(RoomSession session, string reason)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_session, session))
            {
                return;
            }

            LastEndReason = reason;
        }

        _log?.Invoke($"session {session.RoomCode} ended: {reason}");
        await StopAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        RoomSession? session;
        lock (_lock)
        {
            session = _session;
            _session = null;
            _role = null;
        }

        CODConnect.Wifi.WifiHotspot? hotspot;
        lock (_lock)
        {
            hotspot = _wifiNetwork;
            _wifiNetwork = null;
        }

        if (session is not null)
        {
            _log?.Invoke($"stopping session {session.RoomCode}");
            await session.DisposeAsync().ConfigureAwait(false);
            _journal?.Clear();
        }

        if (hotspot is not null)
        {
            await StopWifiAsync().ConfigureAwait(false);
        }
    }

    private async Task StopWifiAsync()
    {
        if (_wifi is null)
        {
            return;
        }

        try
        {
            await _wifi.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Wi-Fi cleanup deferred to next start: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task<RoomSession> StartAsync(
        string? roomCode,
        Func<RoomSessionOptions, CancellationToken, Task<RoomSession>> run,
        string? displayName,
        string? adapter,
        bool allowInternetAdapter,
        bool wifi,
        CancellationToken cancellationToken)
    {
        var auto = _transport as AutoRoomTransport;
        auto?.Progress = Report;
        try
        {
            return await StartReportingAsync(roomCode, run, displayName, adapter, allowInternetAdapter, wifi, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _progress = null;
            auto?.Progress = null;
        }
    }

    private void Report(string step) => _progress = step;

    private async Task<RoomSession> StartReportingAsync(
        string? roomCode,
        Func<RoomSessionOptions, CancellationToken, Task<RoomSession>> run,
        string? displayName,
        string? adapter,
        bool allowInternetAdapter,
        bool wifi,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("A session is already running; stop it first.");
            }
        }

        CODConnect.Wifi.WifiHotspot? hotspot = null;
        if (wifi)
        {
            Report("Starting the console Wi-Fi…");
            if (_wifi is null)
            {
                throw new InvalidOperationException("Wi-Fi mode is not available in this build.");
            }

            var room = new CODConnect.Core.Rooms.RoomOptions();
            hotspot = await _wifi.StartAsync(cancellationToken, (room.PcAddress.ToString(), room.SubnetMask.ToString()), room.GatewayAddress.ToString()).ConfigureAwait(false);
            adapter = hotspot.AdapterName;
            allowInternetAdapter = false;
        }

        var options = new RoomSessionOptions
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Player" : displayName.Trim(),
            RoomCode = roomCode,
            Rendezvous = _dev?.Rendezvous ?? _rendezvous,
            FriendsConsoleAccess = () => _dev?.FriendsConsoleAccess == true,
            RelayOnly = _dev?.RelayOnly == true,
            Transport = _transport,
            Progress = Report,
            ConsoleFactory = () => _consoleFactory(adapter, allowInternetAdapter),
            ReleasePcGateway = hotspot is not null && _wifi is { } wifiRoom ? wifiRoom.ReleaseConsoleGatewayAsync : null,
        };

        RoomSession session;
        try
        {
            session = await run(options, cancellationToken).ConfigureAwait(false);
        }
        catch when (hotspot is not null)
        {
            await StopWifiAsync().ConfigureAwait(false);
            throw;
        }

        lock (_lock)
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("A session started concurrently.");
            }

            _session = session;
            _role = session.Role;
            _wifiNetwork = hotspot;
            LastEndReason = null;
        }

        _journal?.Write(new SessionRecord(session.RoomCode, session.MemberId, session.MemberSecret, session.AdminKey));
        session.Ended += reason => _ = EndAsync(session, reason);
        session.StatusChanged += status => _log?.Invoke($"session {session.RoomCode}: {status}");
        return session;
    }
}
