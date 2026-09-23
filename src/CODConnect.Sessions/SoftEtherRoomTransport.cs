using System.Security.Cryptography;
using CODConnect.SoftEther;

namespace CODConnect.Sessions;

public sealed record SoftEtherTransportOptions
{
    public required Uri ServerBaseUrl { get; init; }

    public required string AdminPassword { get; init; }

    public string NicName { get; init; } = SessionDefaults.SoftEtherNicName;

    public bool PreferInstallerAdapter { get; init; } = true;

    public bool ClearOwnSettingsFirst { get; init; } = true;

    public int AdvertisedPort { get; init; } = SessionDefaults.SoftEtherPort;

    public string LocalServerHost { get; init; } = "127.0.0.1";

    public bool EnableAzureRelay { get; init; } = true;

    public bool AcceptAnyCertificate { get; init; } = true;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan AdapterWaitTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public string AccountPrefix { get; init; } = "codconnect";

    public string InternetNicName { get; init; } = SessionDefaults.InternetNicName;

    public Action<string>? Log { get; init; }
}

public sealed class SoftEtherRoomTransport : IRoomTransport, IConsoleInternetProvider
{
    private string HostAccount => _options.AccountPrefix + "-host";
    private string JoinAccount => _options.AccountPrefix + "-join";
    private string InternetAccount => _options.AccountPrefix + "-internet";
    private const string RoomUser = "room";
    private const string GatewayUser = "gateway";

    private readonly SoftEtherTransportOptions _options;
    private readonly ISoftEtherAdmin? _adminOverride;
    private readonly ISoftEtherClientControl _client;
    private readonly Func<IReadOnlyList<PcapAdapterInfo>> _listPcapAdapters;
    private readonly Func<PcapAdapterInfo, IConsoleNetworkInterface> _portFactory;
    private readonly Func<IReadOnlyList<string>> _lanAddresses;
    private readonly Func<DateTimeOffset> _clock;

    public SoftEtherRoomTransport(
        SoftEtherTransportOptions options,
        ISoftEtherClientControl? clientControl = null,
        ISoftEtherAdmin? adminOverride = null,
        Func<IReadOnlyList<PcapAdapterInfo>>? listPcapAdapters = null,
        Func<PcapAdapterInfo, IConsoleNetworkInterface>? portFactory = null,
        Func<IReadOnlyList<string>>? lanAddresses = null,
        Func<DateTimeOffset>? clock = null)
    {
        _options = options;
        _adminOverride = adminOverride;
        _client = clientControl ?? new VpncmdClientControl();
        _listPcapAdapters = listPcapAdapters ?? (() => PcapAdapters.Enumerate());
        _portFactory = portFactory ?? (info => new NpcapConsoleInterface(info));
        _lanAddresses = lanAddresses ?? TcpRoomTransport.AdvertiseAddresses;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    private readonly object _roomGate = new();
    private (string Hub, string Password)? _room;

    private int _hostGeneration;
    private int _joinGeneration;

    public string ConnectionKind { get; private set; } = "Direct";

    public string? Underlay { get; private set; }

    public async Task<(IConsoleNetworkInterface Port, EndpointInfo Advertise)> HostAsync(CancellationToken cancellationToken = default)
    {
        var admin = _adminOverride ?? SoftEtherJsonRpcClient.Create(_options.ServerBaseUrl, _options.AdminPassword, _options.AcceptAnyCertificate);

        (string Hub, string Password) room;
        bool reusing;
        lock (_roomGate)
        {
            reusing = _room is not null;
            room = _room ??= ("COD" + RandomToken(8), RandomToken(32));
        }

        var hub = room.Hub;
        var password = room.Password;

        if (!reusing)
        {
            await admin.CreateHubAsync(hub, _options.AdminPassword, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (!reusing)
            {
                await admin.CreateUserAsync(hub, RoomUser, password, _options.AdminPassword, cancellationToken, unlimitedBroadcasts: true).ConfigureAwait(false);
            }

            var addresses = new List<string>();
            var ddns = await TryAsync(() => admin.GetDdnsFqdnAsync(_options.AdminPassword, cancellationToken)).ConfigureAwait(false);
            if (ddns is not null)
            {
                addresses.Add(ddns);
            }

            addresses.AddRange(_lanAddresses().Where(a => !addresses.Contains(a)));

            if (_options.EnableAzureRelay)
            {
                var azure = await TryAsync(() => admin.EnableAzureRelayAsync(_options.AdminPassword, cancellationToken)).ConfigureAwait(false);
                if (azure is not null)
                {
                    addresses.Add(azure);
                }
            }

            await PrepareAdapterAsync(cancellationToken).ConfigureAwait(false);
            await _client.ConnectAsync(HostAccount, _options.LocalServerHost, _options.AdvertisedPort, hub, RoomUser, password, NicName, cancellationToken).ConfigureAwait(false);
            var status = await WaitForSessionAsync(HostAccount, cancellationToken).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("The local SoftEther client did not join the room hub on this PC's own server.");
            Underlay = status.Underlay;
            ConnectionKind = "Direct";
            _options.Log?.Invoke($"hosting room hub {hub}; advertising {string.Join(", ", addresses)} port {_options.AdvertisedPort}");

            var port = await AttachAsync(cancellationToken).ConfigureAwait(false);
            await ReapplyBindingsAsync(cancellationToken).ConfigureAwait(false);
            var peerProbe = new HubPeerProbe(admin, hub, _options.AdminPassword, _clock);
            var generation = Interlocked.Increment(ref _hostGeneration);
            var dataPort = new SoftEtherDataPort(
                port,
                () => generation == Volatile.Read(ref _hostGeneration) ? CleanupAsync(admin, HostAccount, hub) : Task.CompletedTask,
                peerProbe.PeerPresent);
            return (dataPort, new EndpointInfo(addresses, _options.AdvertisedPort, hub, RoomUser, password));
        }
        catch
        {
            await CleanupAsync(admin, HostAccount, hub).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IConsoleNetworkInterface> JoinAsync(IReadOnlyList<EndpointInfo> peers, CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        await PrepareAdapterAsync(cancellationToken).ConfigureAwait(false);

        foreach (var peer in peers)
        {
            if (peer.Hub is null || peer.Username is null || peer.Password is null)
            {
                failures.Add($"peer on port {peer.Port} advertised no SoftEther hub credentials (direct-TCP host?)");
                continue;
            }

            foreach (var address in peer.Addresses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _client.ConnectAsync(JoinAccount, address, peer.Port, peer.Hub, peer.Username, peer.Password, NicName, cancellationToken).ConfigureAwait(false);
                    var status = await WaitForSessionAsync(JoinAccount, cancellationToken).ConfigureAwait(false);
                    if (status is null)
                    {
                        _options.Log?.Invoke($"no session via {address}:{peer.Port} within {_options.ConnectTimeout.TotalSeconds:0}s; trying next address");
                        failures.Add($"{address}:{peer.Port} (no session within {_options.ConnectTimeout.TotalSeconds:0}s)");
                        await SafeDisconnectAsync(JoinAccount).ConfigureAwait(false);
                        continue;
                    }

                    Underlay = status.Underlay;
                    ConnectionKind = IsRelayAddress(address) ? "Relay" : "Direct";
                    _options.Log?.Invoke($"joined room hub via {address}:{peer.Port} ({ConnectionKind}); underlay: {status.Underlay ?? "unknown"}");
                    var port = await AttachAsync(cancellationToken).ConfigureAwait(false);
                    await ReapplyBindingsAsync(cancellationToken).ConfigureAwait(false);
                    var generation = Interlocked.Increment(ref _joinGeneration);
                    return new SoftEtherDataPort(port, () => generation == Volatile.Read(ref _joinGeneration) ? SafeDisconnectAsync(JoinAccount) : Task.CompletedTask);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or SoftEtherRpcException)
                {
                    failures.Add($"{address}:{peer.Port} ({ex.Message})");
                    await SafeDisconnectAsync(JoinAccount).ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException(
            "Could not reach the room host's SoftEther server at any advertised address. " + string.Join("; ", failures));
    }

    public async Task<IConsoleNetworkInterface> StartConsoleInternetAsync(IPv4Address gateway, IPv4Address mask, CancellationToken cancellationToken = default)
    {
        var admin = _adminOverride ?? SoftEtherJsonRpcClient.Create(_options.ServerBaseUrl, _options.AdminPassword, _options.AcceptAnyCertificate);
        var hub = "COD" + RandomToken(8);
        var password = RandomToken(32);
        var nic = _options.InternetNicName;
        await admin.CreateHubAsync(hub, _options.AdminPassword, cancellationToken).ConfigureAwait(false);
        try
        {
            await admin.CreateUserAsync(hub, GatewayUser, password, _options.AdminPassword, cancellationToken).ConfigureAwait(false);
            var mac = RandomNumberGenerator.GetBytes(6);
            mac[0] = 0x5E;
            await admin.EnableSecureNatAsync(hub, new System.Net.IPAddress(gateway.ToArray()), new System.Net.IPAddress(mask.ToArray()), mac, _options.AdminPassword, cancellationToken).ConfigureAwait(false);

            await SafeDisconnectAsync(InternetAccount).ConfigureAwait(false);
            await _client.EnsureVirtualAdapterAsync(nic, cancellationToken).ConfigureAwait(false);
            await _client.DisableIpBindingsAsync(nic, cancellationToken).ConfigureAwait(false);
            await _client.ConnectAsync(InternetAccount, _options.LocalServerHost, _options.AdvertisedPort, hub, GatewayUser, password, nic, cancellationToken).ConfigureAwait(false);
            _ = await WaitForSessionAsync(InternetAccount, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The local SoftEther client did not join the console Internet hub.");
            var adapter = await WaitForAdapterAsync(nic, cancellationToken).ConfigureAwait(false)
                          ?? throw new InvalidOperationException($"SoftEther virtual adapter '{nic}' did not come up for the console Internet gateway.");
            await _client.DisableIpBindingsAsync(nic, cancellationToken).ConfigureAwait(false);
            _options.Log?.Invoke($"console Internet: gateway {gateway} (SecureNAT on hub {hub}, adapter {nic})");
            return new SoftEtherDataPort(_portFactory(adapter), () => StopConsoleInternetAsync(admin, hub));
        }
        catch
        {
            await StopConsoleInternetAsync(admin, hub).ConfigureAwait(false);
            throw;
        }
    }

    private async Task StopConsoleInternetAsync(ISoftEtherAdmin admin, string hub)
    {
        await SafeDisconnectAsync(InternetAccount).ConfigureAwait(false);
        try
        {
            await admin.DeleteHubAsync(hub, _options.AdminPassword).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public static bool IsRoomHubName(string name)
        => name.Length == 11 && name.StartsWith("COD", StringComparison.Ordinal) && name[3..].All(Uri.IsHexDigit);

    public static bool IsRelayAddress(string address)
        => address.EndsWith(".vpnazure.net", StringComparison.OrdinalIgnoreCase);

    private async Task<SoftEtherClientSessionStatus?> WaitForSessionAsync(string account, CancellationToken cancellationToken)
    {
        var deadline = _clock() + _options.ConnectTimeout;
        while (_clock() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await _client.GetStatusAsync(account, cancellationToken).ConfigureAwait(false);
            if (status.Established)
            {
                return status;
            }

            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public bool IpBindingsDisabled { get; private set; }

    public async Task ClearOwnSettingsAsync(CancellationToken cancellationToken = default)
    {
        await ClearRoomSettingsAsync().ConfigureAwait(false);
        await SafeDisconnectAsync(InternetAccount).ConfigureAwait(false);
    }

    private async Task ClearRoomSettingsAsync()
    {
        await SafeDisconnectAsync(HostAccount).ConfigureAwait(false);
        await SafeDisconnectAsync(JoinAccount).ConfigureAwait(false);
    }

    private async Task PrepareAdapterAsync(CancellationToken cancellationToken)
    {
        if (_options.ClearOwnSettingsFirst)
        {
            await ClearRoomSettingsAsync().ConfigureAwait(false);
        }

        if (_options.PreferInstallerAdapter && _listPcapAdapters().Any(a => MatchesNic(a, "VPN")))
        {
            NicName = "VPN";
        }
        else
        {
            NicName = _options.NicName;
            await _client.EnsureVirtualAdapterAsync(NicName, cancellationToken).ConfigureAwait(false);
        }

        IpBindingsDisabled = await _client.DisableIpBindingsAsync(NicName, cancellationToken).ConfigureAwait(false);
    }

    public string NicName { get; private set; } = string.Empty;

    private async Task ReapplyBindingsAsync(CancellationToken cancellationToken)
    {
        IpBindingsDisabled = await _client.DisableIpBindingsAsync(NicName, cancellationToken).ConfigureAwait(false) || IpBindingsDisabled;
    }

    private async Task<IConsoleNetworkInterface> AttachAsync(CancellationToken cancellationToken)
    {
        var adapter = await WaitForAdapterAsync(NicName, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException(
                          $"SoftEther virtual adapter '{NicName}' did not come up for packet capture.");
        return _portFactory(adapter);
    }

    private sealed class HubPeerProbe(ISoftEtherAdmin admin, string hub, string adminPassword, Func<DateTimeOffset> clock)
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
        private readonly object _lock = new();
        private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
        private bool _present;
        private Task? _refresh;

        public bool PeerPresent()
        {
            lock (_lock)
            {
                if (clock() - _checkedAt >= Interval && (_refresh is null || _refresh.IsCompleted))
                {
                    _refresh = RefreshAsync();
                }

                return _present;
            }
        }

        private async Task RefreshAsync()
        {
            bool present;
            try
            {
                var hubs = await admin.ListHubsAsync(adminPassword).ConfigureAwait(false);
                present = hubs.FirstOrDefault(h => h.Name == hub)?.SessionCount >= 2;
            }
            catch
            {
                return;
            }

            lock (_lock)
            {
                _present = present;
                _checkedAt = clock();
            }
        }
    }

    private async Task<PcapAdapterInfo?> WaitForAdapterAsync(string nicName, CancellationToken cancellationToken)
    {
        var deadline = _clock() + _options.AdapterWaitTimeout;
        while (_clock() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var adapter = _listPcapAdapters().FirstOrDefault(a => MatchesNic(a, nicName) && a.LinkState == LinkState.Up);
            if (adapter is not null)
            {
                return adapter;
            }

            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public static bool MatchesNic(PcapAdapterInfo adapter, string nicName)
    {
        static bool Match(string text, string nic)
            => text.StartsWith(nic + " ", StringComparison.OrdinalIgnoreCase)
               || text.Equals(nic, StringComparison.OrdinalIgnoreCase)
               || text.EndsWith(" - " + nic, StringComparison.OrdinalIgnoreCase);

        return Match(adapter.FriendlyName, nicName) || Match(adapter.PcapName, nicName);
    }

    private async Task CleanupAsync(ISoftEtherAdmin admin, string account, string hub)
    {
        lock (_roomGate)
        {
            _room = null;
        }

        await SafeDisconnectAsync(account).ConfigureAwait(false);
        try
        {
            await admin.DeleteHubAsync(hub, _options.AdminPassword).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task SafeDisconnectAsync(string account)
    {
        try
        {
            await _client.DisconnectAsync(account).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task<T?> TryAsync<T>(Func<Task<T?>> action) where T : class
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static string RandomToken(int hexChars)
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(hexChars / 2));

    private sealed class SoftEtherDataPort(IConsoleNetworkInterface inner, Func<Task> teardown, Func<bool>? peerPresent = null)
        : IConsoleNetworkInterface, IPeerAwarePort
    {
        public LinkState TransportLinkState => inner.GetLinkState();

        public string Name => inner.Name;

        public MacAddress GetMac() => inner.GetMac();

        public int GetMtu() => inner.GetMtu();

        public LinkState GetLinkState()
            => inner.GetLinkState() == LinkState.Up && (peerPresent?.Invoke() ?? true) ? LinkState.Up : LinkState.Down;

        public CapturedFrame? CaptureFrame(CancellationToken cancellationToken = default) => inner.CaptureFrame(cancellationToken);

        public ValueTask InjectFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
            => inner.InjectFrameAsync(frame, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await teardown().ConfigureAwait(false);
        }
    }
}
