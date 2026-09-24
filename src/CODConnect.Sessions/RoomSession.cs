using CODConnect.Core.Diagnostics;
using CODConnect.Core.Rooms;
using CODConnect.Sessions.Chat;

namespace CODConnect.Sessions;

public enum RoomRole
{
    Host,
    Joiner,
}

public sealed record RoomSessionOptions
{
    public required string DisplayName { get; init; }

    public string? RoomCode { get; init; }

    public required HttpClient Rendezvous { get; init; }

    public required IRoomTransport Transport { get; init; }

    public required Func<Task<IConsoleNetworkInterface>> ConsoleFactory { get; init; }

    public RoomOptions? RoomOptions { get; init; }

    public bool EnableAutoReconnect { get; init; } = true;

    public TimeSpan ReconnectGracePeriod { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan ReconnectAttemptInterval { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan PeerPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan PeerPollTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan RoomWatchdogInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan LeaveTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a friend waits for the host to put the room back after the room server lost it.</summary>
    public TimeSpan RoomRestoreWait { get; init; } = TimeSpan.FromSeconds(75);

    public bool ConsoleInternet { get; init; } = true;

    public Func<Task>? ReleasePcGateway { get; init; }

    public TimeSpan PcInternetPatience { get; init; } = TimeSpan.FromSeconds(60);

    public Func<bool>? FriendsConsoleAccess { get; init; }

    public bool RelayOnly { get; init; }
}

public sealed class RoomSession : IAsyncDisposable
{
    private readonly RoomSessionOptions _options;
    private readonly RendezvousClient _rendezvous;
    private readonly SwitchEngine _engine;
    private readonly UserSpaceSwitch _switch;
    private readonly IConsoleNetworkInterface _console;
    private readonly DhcpServer? _dhcp;
    private readonly RoomDeviceTracker _devices;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _portGate = new();
    public TunnelRoomChat? Chat { get; private set; }

    private IConsoleNetworkInterface _dataPort;
    private int _dataPortId;
    private IConsoleNetworkInterface? _gatewayPort;
    private EndpointInfo? _advertised;
    private readonly int _consolePortId;
    private List<IConsoleNetworkInterface> _sessionPorts;

    private RoomSession(
        RoomSessionOptions options,
        string roomCode,
        string memberId,
        string memberSecret,
        string? adminKey,
        RoomRole role,
        RendezvousClient rendezvous,
        IConsoleNetworkInterface dataPort,
        int dataPortId,
        int consolePortId,
        IConsoleNetworkInterface console,
        UserSpaceSwitch sw,
        SwitchEngine engine,
        DhcpServer? dhcp,
        RoomDeviceTracker devices,
        NetworkCounters counters)
    {
        _options = options;
        RoomCode = roomCode;
        MemberId = memberId;
        MemberSecret = memberSecret;
        AdminKey = adminKey;
        Role = role;
        _rendezvous = rendezvous;
        _dataPort = dataPort;
        _dataPortId = dataPortId;
        _consolePortId = consolePortId;
        _console = console;
        _switch = sw;
        _engine = engine;
        _dhcp = dhcp;
        _devices = devices;
        Counters = counters;
        _sessionPorts = [console, dataPort];

        if (options.EnableAutoReconnect)
        {
            _ = ReconnectLoopAsync(_lifetime.Token);
        }

        _ = RoomWatchdogAsync(_lifetime.Token);
    }

    public string RoomCode { get; }

    public string MemberId { get; }

    public string MemberSecret { get; }

    public string? AdminKey { get; }

    public RoomRole Role { get; }

    public NetworkCounters Counters { get; }

    public DhcpServer? Dhcp => _dhcp;

    public IReadOnlyList<ObservedDevice> Devices => _devices.Snapshot();

    public bool LocalConsoleReady => _devices.IsSideReady(DeviceSide.Local);

    public bool RemoteConsoleReady => _devices.IsSideReady(DeviceSide.Remote);

    public IReadOnlyList<IpcPlayer> Players
    {
        get
        {
            if (Chat?.Snapshot() is not { } chat)
            {
                return [];
            }

            var remote = _devices.Snapshot().Where(d => d.Side == DeviceSide.Remote).ToList();
            return chat.Peers.Select(peer =>
            {
                var console = peer.ConsoleMac is null ? null : remote.FirstOrDefault(d => d.Mac.ToString() == peer.ConsoleMac);
                return new IpcPlayer(peer.Name, peer.Connected, console is null ? null : console.Identity ?? "Unknown device",
                    peer.Connected && console is not null, peer.Relay);
            }).ToList();
        }
    }

    private (byte[]? ConsoleMac, bool Relay) SelfForRoom()
        => (_devices.Snapshot().Where(d => d.Side == DeviceSide.Local).OrderByDescending(d => d.LastSeenUtc).FirstOrDefault()?.Mac.ToArray(),
            _options.Transport.ConnectionKind == "Relay");

    public string? ConsoleInternet { get; private set; }

    public string ConnectionKind => _options.Transport.ConnectionKind;

    public TunnelState TunnelState
        => _dataPort.GetLinkState() == LinkState.Up ? TunnelState.Connected : TunnelState.Connecting;

    public IConsoleNetworkInterface DataPort
    {
        get
        {
            lock (_portGate)
            {
                return _dataPort;
            }
        }
    }

    public event Action<string>? StatusChanged;

    public event Action<string>? Ended;

    private int _endedSignalled;

    private void SignalEnded(string reason)
    {
        if (Interlocked.Exchange(ref _endedSignalled, 1) == 0)
        {
            Ended?.Invoke(reason);
        }
    }

    public static async Task<RoomSession> HostAsync(RoomSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.RoomCode is not null)
        {
            throw new ArgumentException("HostAsync must not specify a RoomCode; use JoinAsync to join an existing room.", nameof(options));
        }

        var rendezvous = new RendezvousClient(options.Rendezvous);
        var (port, advertise) = await options.Transport.HostAsync(cancellationToken).ConfigureAwait(false);
        var created = await rendezvous.CreateRoomAsync(advertise, options.DisplayName, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("Room creation failed at the rendezvous server.");

        var hosted = await WireAsync(options, rendezvous, port, created.RoomCode, created.MemberId, created.MemberSecret, created.AdminKey, RoomRole.Host, cancellationToken).ConfigureAwait(false);
        hosted._advertised = advertise;
        return hosted;
    }

    public static async Task<RoomSession> JoinAsync(RoomSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.RoomCode))
        {
            throw new ArgumentException("JoinAsync requires a RoomCode.", nameof(options));
        }

        var rendezvous = new RendezvousClient(options.Rendezvous);
        var joined = await rendezvous.JoinRoomAsync(options.RoomCode, options.DisplayName, endpoint: null, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Could not join room {options.RoomCode} (unknown, expired, or full).");

        var peerEndpoints = await WaitForPeerEndpointsAsync(options, rendezvous, joined.MemberId, joined.MemberSecret, cancellationToken).ConfigureAwait(false);
        var port = await options.Transport.JoinAsync(OnlyRelayIfAsked(options, peerEndpoints), cancellationToken).ConfigureAwait(false);

        return await WireAsync(options, rendezvous, port, joined.RoomCode, joined.MemberId, joined.MemberSecret, adminKey: null, RoomRole.Joiner, cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (AdminKey is not null)
        {
            await _rendezvous.CloseRoomAsync(RoomCode, AdminKey, cancellationToken).ConfigureAwait(false);
        }

        StatusChanged?.Invoke("room closed");
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();

        await Safely(async () =>
        {
            using var timeout = new CancellationTokenSource(_options.LeaveTimeout);
            if (AdminKey is not null)
            {
                await _rendezvous.CloseRoomAsync(RoomCode, AdminKey, timeout.Token).ConfigureAwait(false);
            }
            else
            {
                await _rendezvous.LeaveAsync(RoomCode, MemberId, MemberSecret, timeout.Token).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        if (Chat is not null)
        {
            await Safely(() => Chat.DisposeAsync().AsTask()).ConfigureAwait(false);
        }

        await Safely(() => _engine.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Safely(() => _console.DisposeAsync().AsTask()).ConfigureAwait(false);

        IConsoleNetworkInterface? gateway;
        lock (_portGate)
        {
            gateway = _gatewayPort;
            _gatewayPort = null;
        }

        if (gateway is not null)
        {
            await Safely(() => gateway.DisposeAsync().AsTask()).ConfigureAwait(false);
        }

        IConsoleNetworkInterface? port;
        lock (_portGate)
        {
            port = _dataPort;
            _dataPort = DeadPort.Instance;
        }

        if (port is not DeadPort)
        {
            await Safely(() => port.DisposeAsync().AsTask()).ConfigureAwait(false);
        }
    }

    private static async Task Safely(Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private LinkState TransportLinkState
    {
        get
        {
            var port = DataPort;
            return port is IPeerAwarePort peerAware ? peerAware.TransportLinkState : port.GetLinkState();
        }
    }

    /// <summary>
    /// Keeps this PC listed in the room. The room server holds rooms in memory, so a restart or
    /// redeploy forgets them while every tunnel is still fine: the host then puts the room back
    /// under the same code, and friends rejoin under the same identity. Only a room its host
    /// closed ends at once; a room that stays missing ends after <see cref="RoomSessionOptions.RoomRestoreWait"/>.
    /// </summary>
    private async Task RoomWatchdogAsync(CancellationToken token)
    {
        if (_options.RoomWatchdogInterval <= TimeSpan.Zero)
        {
            return;
        }

        DateTimeOffset? missingSince = null;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.RoomWatchdogInterval, token).ConfigureAwait(false);
                var result = await _rendezvous.HeartbeatAsync(RoomCode, MemberId, MemberSecret, token).ConfigureAwait(false);
                if (result == HeartbeatResult.RoomClosed)
                {
                    SignalEnded(Role == RoomRole.Host ? "The room was closed." : "The host closed the room.");
                    return;
                }

                if (result == HeartbeatResult.Alive)
                {
                    missingSince = null;
                    continue;
                }

                if (Role == RoomRole.Host && AdminKey is not null && _advertised is not null)
                {
                    var restored = await _rendezvous.RestoreRoomAsync(RoomCode,
                        new RestoreRoomRequest(AdminKey, MemberId, MemberSecret, _options.DisplayName, _advertised), token).ConfigureAwait(false);
                    if (restored == RestoreResult.Restored)
                    {
                        StatusChanged?.Invoke("the room server had lost the room; put it back");
                        missingSince = null;
                        continue;
                    }

                    SignalEnded("The room server gave this room's code to another room. Create a new room.");
                    return;
                }

                if (await _rendezvous.RejoinAsync(RoomCode, MemberId, new RejoinRequest(MemberSecret, _options.DisplayName), token).ConfigureAwait(false))
                {
                    StatusChanged?.Invoke("rejoined the room after the room server lost it");
                    missingSince = null;
                    continue;
                }

                missingSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - missingSince > _options.RoomRestoreWait)
                {
                    SignalEnded("The host left or closed the room.");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
            }
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken token)
    {
        var downSince = TimeSpan.Zero;
        var everUp = TransportLinkState == LinkState.Up;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                if (_console.GetLinkState() != LinkState.Up)
                {
                    _devices.ClearSide(DeviceSide.Local);
                }

                var linkUp = TransportLinkState == LinkState.Up;
                if (linkUp)
                {
                    everUp = true;
                    downSince = TimeSpan.Zero;
                    continue;
                }

                if (!everUp)
                {
                    continue;
                }

                downSince += TimeSpan.FromSeconds(1);
                if (downSince < _options.ReconnectGracePeriod)
                {
                    continue;
                }

                downSince = TimeSpan.Zero;
                await ReestablishAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (RoomGoneException ex)
            {
                SignalEnded(ex.Message);
                return;
            }
            catch
            {
                try
                {
                    await Task.Delay(_options.ReconnectAttemptInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ReestablishAsync(CancellationToken token)
    {
        StatusChanged?.Invoke("reconnecting...");
        IConsoleNetworkInterface replacement;
        if (Role == RoomRole.Host)
        {
            var (newPort, advertise) = await _options.Transport.HostAsync(token).ConfigureAwait(false);
            var advertised = await _rendezvous.UpdateEndpointAsync(RoomCode, MemberId, MemberSecret, advertise, token).ConfigureAwait(false);
            _advertised = advertise;
            if (!advertised)
            {
                await newPort.DisposeAsync().ConfigureAwait(false);
                throw new RoomGoneException("The room expired.");
            }

            replacement = newPort;
        }
        else
        {
            var room = await _rendezvous.GetRoomAsync(RoomCode, MemberId, MemberSecret, token).ConfigureAwait(false);
            if (room is null)
            {
                StatusChanged?.Invoke("room closed");
                throw new RoomGoneException("The host closed the room.");
            }

            var peers = room.Members
                .Where(m => m.MemberId != MemberId && m.Endpoint is not null)
                .Select(m => m.Endpoint!)
                .ToList();
            replacement = await _options.Transport.JoinAsync(OnlyRelayIfAsked(_options, peers), token).ConfigureAwait(false);
        }

        var oldPort = SwapDataPort(replacement);
        if (oldPort is not DeadPort && oldPort is not null)
        {
            await oldPort.DisposeAsync().ConfigureAwait(false);
        }

        StatusChanged?.Invoke("reconnected");
    }

    private IConsoleNetworkInterface? SwapDataPort(IConsoleNetworkInterface replacement)
    {
        replacement = new ChatPort(replacement, frame => Chat?.ReceiveFrame(frame));
        IConsoleNetworkInterface? oldPort;
        lock (_portGate)
        {
            oldPort = _dataPort;
            _engine.RemovePort(_dataPortId);
            _dataPortId = _engine.AddPort(replacement, isConsolePort: false);
            _dataPort = replacement;
            _sessionPorts = [_console, replacement];
            _devices.SetPorts(_consolePortId, replacement.GetMac());
        }

        return oldPort;
    }

    private static IReadOnlyList<EndpointInfo> OnlyRelayIfAsked(RoomSessionOptions options, IReadOnlyList<EndpointInfo> peers)
    {
        if (!options.RelayOnly)
        {
            return peers;
        }

        var relayed = peers
            .Select(p => p with { Addresses = p.Addresses.Where(SoftEtherRoomTransport.IsRelayAddress).ToList() })
            .Where(p => p.Addresses.Count > 0)
            .ToList();
        return relayed.Count > 0
            ? relayed
            : throw new InvalidOperationException("The host offers no relay address. Turn off \u201cAlways join through the relay\u201d on the Developer tab to join directly.");
    }

    private static async Task<IReadOnlyList<EndpointInfo>> WaitForPeerEndpointsAsync(
        RoomSessionOptions options, RendezvousClient rendezvous, string selfMemberId, string selfSecret, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + options.PeerPollTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var room = await rendezvous.GetRoomAsync(options.RoomCode!, selfMemberId, selfSecret, cancellationToken).ConfigureAwait(false);
            if (room is null)
            {
                throw new InvalidOperationException($"Room {options.RoomCode} disappeared (closed or expired).");
            }

            var endpoints = room.Members
                .Where(m => m.MemberId != selfMemberId && m.Endpoint is not null)
                .Select(m => m.Endpoint!)
                .ToList();
            if (endpoints.Count > 0)
            {
                return endpoints;
            }

            await Task.Delay(options.PeerPollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("No peer advertised an endpoint before the poll timeout.");
    }

    private static async Task<RoomSession> WireAsync(
        RoomSessionOptions options,
        RendezvousClient rendezvous,
        IConsoleNetworkInterface dataPort,
        string roomCode,
        string memberId,
        string memberSecret,
        string? adminKey,
        RoomRole role,
        CancellationToken cancellationToken)
    {
        var counters = new NetworkCounters();
        var console = await options.ConsoleFactory().ConfigureAwait(false);
        var guard = new RoomGuard(options.RoomOptions ?? new RoomOptions(), role == RoomRole.Host, options.FriendsConsoleAccess);
        var sw = new UserSpaceSwitch(new SwitchOptions
        {
            StrictConsoleScoping = true,
            LocalMacs = LocalAdapterMacs(),
            TunnelIngressFilter = frame => guard.AllowsFromTunnel(frame.Span),
        }, counters);
        var engine = new SwitchEngine(sw);
        RoomSession? session = null;
        dataPort = new ChatPort(dataPort, frame => session?.Chat?.ReceiveFrame(frame));
        var consolePortId = engine.AddPort(console, isConsolePort: true);
        var dataPortId = engine.AddPort(dataPort, isConsolePort: false);

        var devices = new RoomDeviceTracker(
            consolePortId,
            dataPort.GetMac(),
            tunnelUp: () => dataPort.GetLinkState() == LinkState.Up,
            consoleLinkUp: () => console.GetLinkState() == LinkState.Up);
        sw.FrameAccepted += devices.OnFrame;

        DhcpServer? dhcp = null;
        if (role == RoomRole.Host)
        {
            var roomOptions = (options.RoomOptions ?? new RoomOptions()) with { RoomCode = roomCode };
            dhcp = new DhcpServer(roomOptions, counters);
            dhcp.LeaseGranted += devices.OnLease;
        }

        await engine.StartAsync(cancellationToken).ConfigureAwait(false);
        session = new RoomSession(options, roomCode, memberId, memberSecret, adminKey, role, rendezvous, dataPort, dataPortId, consolePortId, console, sw, engine, dhcp, devices, counters);
        session.Chat = new TunnelRoomChat(roomCode + ":" + memberId, memberId, options.DisplayName,
            // While the room server has lost the room (restart), keep the last member list: the
            // host puts the room back within seconds and nobody should flicker out of it.
            async token => (await rendezvous.GetRoomAsync(roomCode, memberId, memberSecret, token).ConfigureAwait(false))?.Members
                           ?? throw new InvalidOperationException("The room is not listed right now."),
            (frame, token) => session.DataPort is ChatPort port ? port.SendChatAsync(frame, token) : ValueTask.CompletedTask,
            () => session.DataPort.GetLinkState() == LinkState.Up,
            encrypted: () => IsEncryptedTransport(options.Transport),
            self: session.SelfForRoom);
        session.Chat.Start();
        if (dhcp is not null)
        {
            var sessionPorts = new List<IConsoleNetworkInterface> { console, dataPort };
            sw.FrameAccepted += (_, frame) => session.TryServeDhcp(frame, sessionPorts, counters);
        }

        var room = options.RoomOptions ?? new RoomOptions();
        var provider = options.Transport as IConsoleInternetProvider;
        if (options.ConsoleInternet && !room.GatewayAddress.IsNone)
        {
            if (options.ReleasePcGateway is { } release)
            {
                session.WatchPcInternet(room, release, provider);
            }
            else if (provider is not null)
            {
                session.StartConsoleInternet(provider, room.GatewayAddress, room.SubnetMask);
            }
        }

        return session;
    }

    private void WatchPcInternet(RoomOptions room, Func<Task> releasePcGateway, IConsoleInternetProvider? provider)
    {
        ConsoleInternet = "Waiting";
        var watch = new PcInternetWatch(room.ServerAddress, room.SubnetMask, _options.PcInternetPatience);
        void Observe(int portId, CapturedFrame frame)
        {
            if (portId == _consolePortId)
            {
                watch.ObserveConsoleFrame(frame.Buffer.Span);
            }
        }

        _switch.FrameAccepted += Observe;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!watch.Confirmed && !watch.GaveUp)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _switch.FrameAccepted -= Observe;
            }

            if (watch.Confirmed)
            {
                ConsoleInternet = "Ready";
                StatusChanged?.Invoke("console Internet: through Windows' hotspot sharing on this PC");
                return;
            }

            StatusChanged?.Invoke("console Internet: Windows' hotspot sharing did not carry the console's traffic; using CODCONNECT's gateway instead");
            await Safely(releasePcGateway).ConfigureAwait(false);
            if (provider is null)
            {
                ConsoleInternet = "Unavailable";
                return;
            }

            StartConsoleInternet(provider, room.GatewayAddress, room.SubnetMask, announceGateway: true);
        });
    }

    private void StartConsoleInternet(IConsoleInternetProvider provider, IPv4Address gateway, IPv4Address mask, bool announceGateway = false)
    {
        ConsoleInternet = "Starting";
        _ = Task.Run(async () =>
        {
            IConsoleNetworkInterface port;
            try
            {
                port = await provider.StartConsoleInternetAsync(gateway, mask, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ConsoleInternet = "Unavailable";
                if (ex is not OperationCanceledException)
                {
                    StatusChanged?.Invoke("console Internet unavailable: " + ex.Message);
                }

                return;
            }

            var late = false;
            lock (_portGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    late = true;
                }
                else
                {
                    _gatewayPort = port;
                    var portId = _engine.AddGatewayPort(port);
                    _devices.IgnorePort(portId);
                    if (announceGateway)
                    {
                        AnnounceGatewayWhenSeen(portId, gateway);
                    }
                }
            }

            if (late)
            {
                await Safely(() => port.DisposeAsync().AsTask()).ConfigureAwait(false);
                return;
            }

            ConsoleInternet = "Ready";
        });
    }

    private void AnnounceGatewayWhenSeen(int gatewayPortId, IPv4Address gateway)
    {
        var announced = 0;
        void Observe(int portId, CapturedFrame frame)
        {
            if (portId != gatewayPortId
                || !EthernetFrame.TryParse(frame.Buffer.Span, out var ethernet)
                || !SentBy(ethernet, gateway)
                || Interlocked.Exchange(ref announced, 1) != 0)
            {
                return;
            }

            _switch.FrameAccepted -= Observe;
            var announcement = ArpPacket.BuildEthernetFrame(ethernet.Source, gateway, gateway, isReply: false, MacAddress.None);
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < 3 && !_lifetime.IsCancellationRequested; i++)
                {
                    await Safely(() => GetPorts().Console.InjectFrameAsync(announcement).AsTask()).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
            });
        }

        _switch.FrameAccepted += Observe;
    }

    private static bool SentBy(ParsedFrame ethernet, IPv4Address address)
    {
        if (ethernet.EtherType == EthernetFrame.EtherTypeArp)
        {
            return ArpPacket.TryParse(ethernet.Payload, out var arp) && arp.SenderIp == address;
        }

        return ethernet.EtherType == EthernetFrame.EtherTypeIpv4
               && IPv4Packet.TryParse(ethernet.Payload, out var ip)
               && ip.Source == address;
    }

    private void TryServeDhcp(CapturedFrame frame, List<IConsoleNetworkInterface> ports, NetworkCounters counters)
    {
        try
        {
            if (_dhcp is null
                || !EthernetFrame.TryParse(frame.Buffer.Span, out var parsed)
                || parsed.EtherType != EthernetFrame.EtherTypeIpv4
                || !IPv4Packet.TryParse(parsed.Payload, out var ip)
                || ip.Protocol != IPv4Packet.ProtocolUdp
                || !UdpPacket.TryParse(ip.Payload, out var udp)
                || udp.DestinationPort != UdpPacket.PortDhcpServer)
            {
                return;
            }

            foreach (var reply in _dhcp.HandleFrame(frame, ports[0].GetMac()))
            {
                var (currentConsole, currentData) = GetPorts();
                foreach (var port in new[] { currentConsole, currentData })
                {
                    port.InjectFrameAsync(reply).AsTask().GetAwaiter().GetResult();
                }
            }
        }
        catch
        {
            counters.RecordDropped();
        }
    }

    private static bool IsEncryptedTransport(IRoomTransport transport) => transport switch
    {
        SoftEtherRoomTransport => true,
        AutoRoomTransport auto => auto.ActiveTransport == "SoftEther",
        _ => false,
    };

    private static HashSet<MacAddress> LocalAdapterMacs()
    {
        var macs = new HashSet<MacAddress>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                var bytes = nic.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length == 6)
                {
                    macs.Add(new MacAddress(bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5]));
                }
            }
        }
        catch
        {
        }

        return macs;
    }

    private (IConsoleNetworkInterface Console, IConsoleNetworkInterface Data) GetPorts()
    {
        lock (_portGate)
        {
            return (_console, _dataPort);
        }
    }

    private sealed class RoomGoneException(string message) : Exception(message);

    private sealed class DeadPort : IConsoleNetworkInterface
    {
        public static readonly DeadPort Instance = new();

        public string Name => "dead";

        public MacAddress GetMac() => MacAddress.None;

        public int GetMtu() => 0;

        public LinkState GetLinkState() => LinkState.Down;

        public CapturedFrame? CaptureFrame(CancellationToken cancellationToken = default) => null;

        public ValueTask InjectFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
