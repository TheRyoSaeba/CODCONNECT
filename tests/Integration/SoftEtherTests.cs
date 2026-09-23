using CODConnect.NetworkSimulation;
using CODConnect.Protocol;
using CODConnect.Sessions;
using CODConnect.SoftEther;

namespace CODConnect.IntegrationTests;

public class SoftEtherTests
{
    private static async Task<(SoftEtherJsonRpcClient Client, string Password)?> LiveServerAsync()
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connect = tcp.ConnectAsync("127.0.0.1", 5555);
            if (!connect.Wait(1500) || !tcp.Connected)
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        foreach (var password in new[] { SoftEtherServerSetup.TryLoad(), string.Empty })
        {
            if (password is null)
            {
                continue;
            }

            var client = SoftEtherJsonRpcClient.Create(SoftEtherServerSetup.DefaultServerUrl, password);
            if (await client.TestAsync(password))
            {
                return (client, password);
            }
        }

        return null;
    }

    [Fact]
    public async Task Live_ListHubs_ReturnsHubList_WhenServerRunning()
    {
        if (await LiveServerAsync() is not { } live)
        {
            return;
        }

        var hubs = await live.Client.ListHubsAsync(live.Password);
        Assert.Contains(hubs, h => h.Name == "DEFAULT");
    }

    [Fact]
    public async Task Live_CreateHubAndUser_RoundTrip()
    {
        if (await LiveServerAsync() is not { } live)
        {
            return;
        }

        var hubName = $"CODTEST{Guid.NewGuid():N}".ToUpper()[..16];
        await live.Client.CreateHubAsync(hubName, live.Password);
        try
        {
            await live.Client.CreateUserAsync(hubName, "room", "pw-" + Guid.NewGuid().ToString("N"), live.Password);
            var hubs = await live.Client.ListHubsAsync(live.Password);
            Assert.Contains(hubs, h => h.Name == hubName);
            Assert.NotNull(await live.Client.GetDdnsFqdnAsync(live.Password));
        }
        finally
        {
            await live.Client.DeleteHubAsync(hubName, live.Password);
        }

        Assert.DoesNotContain(await live.Client.ListHubsAsync(live.Password), h => h.Name == hubName);
    }

    [Fact]
    public async Task Live_WrongPassword_IsRejected()
    {
        if (await LiveServerAsync() is not { } live || live.Password.Length == 0)
        {
            return;
        }

        var client = SoftEtherJsonRpcClient.Create(SoftEtherServerSetup.DefaultServerUrl, "wrong-password");
        Assert.False(await client.TestAsync("wrong-password"));
    }

    [Fact]
    public async Task Live_Transport_FrameCrossesRealSoftEtherHub()
    {
        if (!SoftEtherServerSetup.IsServerInstalled || !VpncmdClientControl.IsInstalled || !PcapAdapters.IsAvailable)
        {
            return;
        }

        var adminPassword = await SoftEtherServerSetup.EnsureAdminPasswordAsync();
        if (adminPassword is null)
        {
            return;
        }

        if (await LiveRoomRunningAsync())
        {
            return;
        }

        var host = new SoftEtherRoomTransport(new SoftEtherTransportOptions
        {
            ServerBaseUrl = SoftEtherServerSetup.DefaultServerUrl,
            AdminPassword = adminPassword,
            AccountPrefix = "codconnect-selftest",
            NicName = "VPN9",
            PreferInstallerAdapter = false,
            ClearOwnSettingsFirst = false,
            EnableAzureRelay = false,
        });
        var joiner = new SoftEtherRoomTransport(new SoftEtherTransportOptions
        {
            ServerBaseUrl = SoftEtherServerSetup.DefaultServerUrl,
            AdminPassword = adminPassword,
            AccountPrefix = "codconnect-selftest",
            NicName = "VPN8",
            PreferInstallerAdapter = false,
            ClearOwnSettingsFirst = false,
        });

        var (hostPort, advertise) = await host.HostAsync();
        try
        {
            Assert.NotNull(advertise.Hub);
            Assert.Contains(advertise.Addresses, a => a.EndsWith(".softether.net"));

            var joinPort = await joiner.JoinAsync([advertise with { Addresses = ["127.0.0.1"] }]);
            try
            {
                Assert.Equal("Direct", joiner.ConnectionKind);
                Assert.NotEqual(hostPort.GetMac(), joinPort.GetMac());

                var probe = ArpPacket.BuildEthernetFrame(
                    hostPort.GetMac(), new IPv4Address(10, 42, 0, 1), new IPv4Address(10, 42, 0, 2),
                    isReply: false, replyDestinationMac: MacAddress.None);
                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
                var crossed = false;
                while (!crossed && DateTimeOffset.UtcNow < deadline)
                {
                    await hostPort.InjectFrameAsync(probe);
                    var until = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(500);
                    while (DateTimeOffset.UtcNow < until)
                    {
                        using var cts = new CancellationTokenSource(200);
                        CapturedFrame? frame;
                        try { frame = joinPort.CaptureFrame(cts.Token); } catch (OperationCanceledException) { break; }
                        if (frame is { } captured && captured.Buffer.Span.SequenceEqual(probe))
                        {
                            crossed = true;
                            break;
                        }
                    }
                }

                Assert.True(crossed, "the probe frame injected on the host's VPN9 adapter never arrived on the joiner's VPN8 adapter");
            }
            finally
            {
                await joinPort.DisposeAsync();
            }
        }
        finally
        {
            await hostPort.DisposeAsync();
        }

        var admin = SoftEtherJsonRpcClient.Create(SoftEtherServerSetup.DefaultServerUrl, adminPassword);
        Assert.DoesNotContain(await admin.ListHubsAsync(adminPassword), h => h.Name == advertise.Hub);
    }

    [Fact]
    public async Task Transport_HostAsync_CreatesRoomHubAndUser_AdvertisesDdnsThenLanThenAzure()
    {
        var admin = new FakeAdmin { DdnsFqdn = "vpn123.softether.net", AzureHostName = "vpn123" };
        var client = new FakeClientControl();
        var port = new RecordingPort("vpn-port", MacAddress.None);
        var transport = new SoftEtherRoomTransport(
            Options(),
            clientControl: client,
            adminOverride: admin,
            listPcapAdapters: () => [MakeAdapter("Ethernet"), MakeAdapter("VPN9 - VPN Client")],
            portFactory: _ => port,
            lanAddresses: () => ["192.168.1.20", "10.0.0.5"]);

        var (dataPort, advertise) = await transport.HostAsync();

        var hub = Assert.Single(admin.Hubs);
        Assert.StartsWith("COD", hub);
        var user = Assert.Single(admin.Users);
        Assert.Equal((hub, "room"), (user.Hub, user.Name));
        Assert.Equal(32, user.Password.Length);
        Assert.Contains("room", admin.UnlimitedBroadcastUsers);

        Assert.Equal(["vpn123.softether.net", "192.168.1.20", "10.0.0.5", "vpn123.vpnazure.net"], advertise.Addresses);
        Assert.Equal(5555, advertise.Port);
        Assert.Equal((hub, "room", user.Password), (advertise.Hub, advertise.Username, advertise.Password));

        Assert.Equal(["VPN9"], client.EnsuredAdapters);
        var connect = Assert.Single(client.Connects);
        Assert.Equal(("codconnect-host", "127.0.0.1", 5555, hub, "room", user.Password, "VPN9"), connect);
        Assert.Equal("Direct", transport.ConnectionKind);
        Assert.Equal("Direct", transport.ConnectionKind);

        await dataPort.DisposeAsync();
        Assert.Contains("codconnect-host", client.Disconnects);
        Assert.Empty(admin.Hubs);
        Assert.Equal(LinkState.Down, port.GetLinkState());
    }

    [Fact]
    public async Task Transport_HostPort_SeparatesPeerPresenceFromTransportHealth()
    {
        var admin = new FakeAdmin();
        var client = new FakeClientControl();
        var transport = new SoftEtherRoomTransport(
            Options(),
            clientControl: client,
            adminOverride: admin,
            listPcapAdapters: () => [MakeAdapter("VPN9 - VPN Client")],
            portFactory: _ => new RecordingPort("vpn-port", MacAddress.None));

        var (port, _) = await transport.HostAsync();

        Assert.Equal(LinkState.Down, port.GetLinkState());
        Assert.Equal(LinkState.Up, ((IPeerAwarePort)port).TransportLinkState);
        await port.DisposeAsync();
    }

    [Fact]
    public async Task Transport_HostAsync_Again_ReusesTheSameHubAndCredentials()
    {
        var admin = new FakeAdmin();
        var transport = new SoftEtherRoomTransport(
            Options(),
            clientControl: new FakeClientControl(),
            adminOverride: admin,
            listPcapAdapters: () => [MakeAdapter("VPN9 - VPN Client")],
            portFactory: _ => new RecordingPort("vpn-port", MacAddress.None));

        var (_, first) = await transport.HostAsync();
        var (_, second) = await transport.HostAsync();

        Assert.Equal((first.Hub, first.Password), (second.Hub, second.Password));
        Assert.Single(admin.Hubs);
        Assert.Single(admin.Users);
    }

    [Fact]
    public async Task Transport_ConsoleInternet_BuildsLocalNatHub_AndRemovesItOnDispose()
    {
        var admin = new FakeAdmin();
        var client = new FakeClientControl();
        var transport = new SoftEtherRoomTransport(
            Options(),
            clientControl: client,
            adminOverride: admin,
            listPcapAdapters: () => [MakeAdapter("VPN9 - VPN Client"), MakeAdapter("VPN8 - VPN Client")],
            portFactory: _ => new RecordingPort("gateway-port", MacAddress.None));

        var port = await transport.StartConsoleInternetAsync(new IPv4Address(10, 42, 0, 254), new IPv4Address(255, 255, 255, 0));

        var hub = Assert.Single(admin.Hubs);
        Assert.True(SoftEtherRoomTransport.IsRoomHubName(hub));
        var nat = Assert.Single(admin.SecureNats);
        Assert.Equal((hub, "10.42.0.254", "255.255.255.0"), (nat.Hub, nat.Address.ToString(), nat.Mask.ToString()));
        Assert.Equal(0x5E, nat.Mac[0]);
        var connect = Assert.Single(client.Connects);
        Assert.Equal(("codconnect-internet", "127.0.0.1", hub, "gateway", "VPN8"), (connect.Account, connect.Host, connect.Hub, connect.User, connect.Nic));
        Assert.Contains("VPN8", client.EnsuredAdapters);

        await port.DisposeAsync();
        Assert.Contains("codconnect-internet", client.Disconnects);
        Assert.Empty(admin.Hubs);
    }

    [Fact]
    public async Task Transport_ConsoleInternet_WhenClientNeverJoins_CleansUpAndThrows()
    {
        var admin = new FakeAdmin();
        var client = new FakeClientControl { NeverEstablish = true };
        var transport = new SoftEtherRoomTransport(
            Options() with { ConnectTimeout = TimeSpan.FromMilliseconds(300) },
            clientControl: client,
            adminOverride: admin,
            listPcapAdapters: () => [MakeAdapter("VPN8 - VPN Client")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.StartConsoleInternetAsync(new IPv4Address(10, 42, 0, 254), new IPv4Address(255, 255, 255, 0)));
        Assert.Empty(admin.Hubs);
        Assert.Contains("codconnect-internet", client.Disconnects);
    }

    [Fact]
    public async Task Transport_DisposingAReplacedHostPort_KeepsTheNewConnection()
    {
        var admin = new FakeAdmin();
        var client = new FakeClientControl();
        var transport = new SoftEtherRoomTransport(
            Options(),
            clientControl: client,
            adminOverride: admin,
            listPcapAdapters: () => [MakeAdapter("VPN9 - VPN Client")],
            portFactory: _ => new RecordingPort("vpn-port", MacAddress.None));

        var (old, _) = await transport.HostAsync();
        var (current, _) = await transport.HostAsync();
        var disconnectsBefore = client.Disconnects.Count;

        await old.DisposeAsync();
        Assert.Equal(disconnectsBefore, client.Disconnects.Count);
        Assert.Single(admin.Hubs);

        await current.DisposeAsync();
        Assert.Contains("codconnect-host", client.Disconnects.Skip(disconnectsBefore));
        Assert.Empty(admin.Hubs);
    }

    [Fact]
    public async Task Transport_DisposingAReplacedJoinPort_KeepsTheNewConnection()
    {
        var client = new FakeClientControl();
        var transport = new SoftEtherRoomTransport(
            Options(),
            clientControl: client,
            adminOverride: new FakeAdmin(),
            listPcapAdapters: () => [MakeAdapter("VPN9 - VPN Client")],
            portFactory: _ => new RecordingPort("vpn-port", MacAddress.None));
        var peer = new EndpointInfo(["192.168.1.20"], 5555, "CODABCD1234", "room", "pw");

        var old = await transport.JoinAsync([peer]);
        var current = await transport.JoinAsync([peer]);
        var disconnectsBefore = client.Disconnects.Count;

        await old.DisposeAsync();
        Assert.Equal(disconnectsBefore, client.Disconnects.Count);

        await current.DisposeAsync();
        Assert.Contains("codconnect-join", client.Disconnects.Skip(disconnectsBefore));
    }

    [Fact]
    public async Task Transport_ReHosting_LeavesTheConsoleInternetConnectionAlone()
    {
        var client = new FakeClientControl();
        var transport = new SoftEtherRoomTransport(
            Options(),
            clientControl: client,
            adminOverride: new FakeAdmin(),
            listPcapAdapters: () => [MakeAdapter("VPN9 - VPN Client")],
            portFactory: _ => new RecordingPort("vpn-port", MacAddress.None));

        await transport.HostAsync();
        await transport.HostAsync();

        Assert.DoesNotContain("codconnect-internet", client.Disconnects);
    }

    [Fact]
    public async Task Transport_ClearOwnSettings_AlsoRemovesTheConsoleInternetSetting()
    {
        var client = new FakeClientControl();
        var transport = new SoftEtherRoomTransport(Options(), clientControl: client, adminOverride: new FakeAdmin(), listPcapAdapters: () => []);

        await transport.ClearOwnSettingsAsync();

        Assert.Equal(["codconnect-host", "codconnect-join", "codconnect-internet"], client.Disconnects);
    }

    [Fact]
    public async Task Transport_HostAsync_WhenLocalClientNeverEstablishes_CleansUpAndThrows()
    {
        var admin = new FakeAdmin();
        var client = new FakeClientControl { NeverEstablish = true };
        var transport = new SoftEtherRoomTransport(
            Options() with { ConnectTimeout = TimeSpan.FromMilliseconds(300) },
            clientControl: client,
            adminOverride: admin,
            listPcapAdapters: () => [MakeAdapter("VPN9 - VPN Client")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.HostAsync());
        Assert.Empty(admin.Hubs);
        Assert.Contains("codconnect-host", client.Disconnects);
    }

    [Fact]
    public async Task Transport_JoinAsync_TriesAddressesInOrder_ReportsRelayForAzure()
    {
        var client = new FakeClientControl { FailHosts = { "vpn123.softether.net", "192.168.1.20" } };
        var transport = new SoftEtherRoomTransport(
            Options() with { ConnectTimeout = TimeSpan.FromMilliseconds(300) },
            clientControl: client,
            listPcapAdapters: () => [MakeAdapter("VPN9 - VPN Client")],
            portFactory: _ => new RecordingPort("vpn-port", MacAddress.None));

        var peer = new EndpointInfo(["vpn123.softether.net", "192.168.1.20", "vpn123.vpnazure.net"], 5555, "CODABCD", "room", "secret");
        var port = await transport.JoinAsync([peer]);

        Assert.NotNull(port);
        Assert.Equal(["vpn123.softether.net", "192.168.1.20", "vpn123.vpnazure.net"], client.Connects.Select(c => c.Host));
        Assert.All(client.Connects, c => Assert.Equal(("codconnect-join", 5555, "CODABCD", "room", "secret", "VPN9"), (c.Account, c.Port, c.Hub, c.User, c.Password, c.Nic)));
        Assert.Equal("Relay", transport.ConnectionKind);

        await port.DisposeAsync();
        Assert.Contains("codconnect-join", client.Disconnects);
    }

    [Fact]
    public async Task Transport_JoinAsync_DirectAddress_ReportsDirect()
    {
        var client = new FakeClientControl();
        var transport = new SoftEtherRoomTransport(
            Options(),
            clientControl: client,
            listPcapAdapters: () => [MakeAdapter("VPN9 - VPN Client")],
            portFactory: _ => new RecordingPort("vpn-port", MacAddress.None));

        await transport.JoinAsync([new EndpointInfo(["vpn123.softether.net"], 5555, "CODABCD", "room", "secret")]);
        Assert.Equal("Direct", transport.ConnectionKind);
    }

    [Fact]
    public async Task Transport_JoinAsync_PeerWithoutCredentials_IsSkippedWithExplanation()
    {
        var transport = new SoftEtherRoomTransport(Options(), clientControl: new FakeClientControl());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.JoinAsync([new EndpointInfo(["10.0.0.1"], 47777)]));
        Assert.Contains("no SoftEther hub credentials", error.Message);
    }

    [Fact]
    public async Task Transport_HostAsync_WithoutVpnAdapter_TimesOutWithHelpfulError()
    {
        var admin = new FakeAdmin();
        var transport = new SoftEtherRoomTransport(
            Options() with { AdapterWaitTimeout = TimeSpan.FromMilliseconds(500) },
            clientControl: new FakeClientControl(),
            adminOverride: admin,
            listPcapAdapters: () => [MakeAdapter("Ethernet")]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.HostAsync());
        Assert.Contains("virtual adapter", error.Message);
        Assert.Empty(admin.Hubs);
    }

    [Theory]
    [InlineData("VPN9 - VPN Client", true)]
    [InlineData("VPN Client Adapter - VPN9", true)]
    [InlineData("VPN - VPN Client", false)]
    [InlineData("VPN99 - VPN Client", false)]
    [InlineData("Ethernet", false)]
    public void MatchesNic_IsExactOnTheAdapterName(string friendlyName, bool expected)
        => Assert.Equal(expected, SoftEtherRoomTransport.MatchesNic(MakeAdapter(friendlyName), "VPN9"));

    [Fact]
    public void Vpncmd_ParseTable_ReadsSessionStatus()
    {
        const string output = """
            VPN Client>AccountStatusGet
            AccountStatusGet command - Get Current VPN Connection Setting Status
            Item                                      |Value
            ------------------------------------------+------------------------
            VPN Connection Setting Name               |codnat
            Session Status                            |Connection Completed (Session Established)
            Physical Underlay Protocol                |VPN over UDP with NAT-T (IPv4)RUDP/UDP
            The command completed successfully.
            """;
        var fields = VpncmdClientControl.ParseTable(output);
        Assert.Equal("Connection Completed (Session Established)", fields["Session Status"]);
        Assert.StartsWith("VPN over UDP with NAT-T", fields["Physical Underlay Protocol"]);
    }

    [Fact]
    public async Task Vpncmd_DisconnectAsync_ToleratesMissingSetting_AndConnectReplacesStaleOne()
    {
        var calls = new List<string>();
        var control = new VpncmdClientControl(vpncmdPath: "vpncmd.exe", run: (args, _) =>
        {
            calls.Add(string.Join(' ', args));
            return Task.FromResult(args[0] switch
            {
                "AccountDisconnect" or "AccountDelete" => new VpncmdResult(VpncmdClientControl.ErrorAccountNotFound, "Error occurred. (Error code: 36)\nObject not found."),
                _ => new VpncmdResult(0, "The command completed successfully."),
            });
        });

        await control.DisconnectAsync("codconnect-join");
        await control.ConnectAsync("codconnect-join", "host.example", 5555, "CODABCD", "room", "pw", "VPN9");

        Assert.Equal(
            [
                "AccountDisconnect codconnect-join",
                "AccountDelete codconnect-join",
                "AccountDisconnect codconnect-join",
                "AccountDelete codconnect-join",
                "AccountCreate codconnect-join /SERVER:host.example:5555 /HUB:CODABCD /USERNAME:room /NICNAME:VPN9",
                "AccountPasswordSet codconnect-join /PASSWORD:pw /TYPE:standard",
                "AccountConnect codconnect-join",
            ],
            calls);
    }

    private static async Task<bool> LiveRoomRunningAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var reply = await new ServiceIpcClient().SendAsync(new IpcRequest("status"), timeout.Token);
            return reply.Status?.HasSession == true;
        }
        catch
        {
            return false;
        }
    }

    private static SoftEtherTransportOptions Options() => new()
    {
        ServerBaseUrl = SoftEtherServerSetup.DefaultServerUrl,
        AdminPassword = "test-admin",
        AdapterWaitTimeout = TimeSpan.FromSeconds(2),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        PollInterval = TimeSpan.FromMilliseconds(20),
    };

    private static PcapAdapterInfo MakeAdapter(string friendlyName)
        => new(
            PcapName: $@"\Device\NPF_{{{Guid.NewGuid()}}}",
            FriendlyName: friendlyName,
            Description: string.Empty,
            Addresses: [new IPv4Address(192, 168, 1, 10)],
            Mac: new MacAddress(0x02, 0x00, 0x00, 0x00, 0x00, 0x09),
            LinkState: LinkState.Up,
            IsLoopback: false);

    private sealed class FakeAdmin : ISoftEtherAdmin
    {
        public List<string> Hubs { get; } = new();
        public List<(string Hub, string Name, string Password)> Users { get; } = new();
        public string? DdnsFqdn { get; init; }
        public string? AzureHostName { get; init; }
        public bool AzureEnabled { get; private set; }

        public Task<IReadOnlyList<SoftEtherHub>> ListHubsAsync(string adminPassword, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SoftEtherHub>>(Hubs.Select(n => new SoftEtherHub(n, true, 0)).ToList());

        public Task CreateHubAsync(string name, string adminPassword, CancellationToken cancellationToken = default)
        {
            Hubs.Add(name);
            return Task.CompletedTask;
        }

        public Task DeleteHubAsync(string name, string adminPassword, CancellationToken cancellationToken = default)
        {
            Hubs.Remove(name);
            return Task.CompletedTask;
        }

        public List<string> UnlimitedBroadcastUsers { get; } = new();

        public Task CreateUserAsync(string hubName, string userName, string password, string adminPassword, CancellationToken cancellationToken = default, bool unlimitedBroadcasts = false)
        {
            if (unlimitedBroadcasts) UnlimitedBroadcastUsers.Add(userName);
            Users.Add((hubName, userName, password));
            return Task.CompletedTask;
        }

        public Task<string?> GetDdnsFqdnAsync(string adminPassword, CancellationToken cancellationToken = default) => Task.FromResult(DdnsFqdn);

        public Task<string?> EnableAzureRelayAsync(string adminPassword, CancellationToken cancellationToken = default)
        {
            AzureEnabled = AzureHostName is not null;
            return Task.FromResult(AzureHostName is null ? null : AzureHostName + ".vpnazure.net");
        }

        public Task SetServerPasswordAsync(string newPassword, string currentPassword, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> TestAsync(string adminPassword, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public List<(string Hub, System.Net.IPAddress Address, System.Net.IPAddress Mask, byte[] Mac)> SecureNats { get; } = new();

        public Task EnableSecureNatAsync(string hubName, System.Net.IPAddress address, System.Net.IPAddress mask, byte[] mac, string adminPassword, CancellationToken cancellationToken = default)
        {
            SecureNats.Add((hubName, address, mask, mac));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClientControl : ISoftEtherClientControl
    {
        private readonly Dictionary<string, string> _connectedHost = new();

        public HashSet<string> FailHosts { get; } = new();
        public bool NeverEstablish { get; init; }
        public List<string> EnsuredAdapters { get; } = new();
        public List<(string Account, string Host, int Port, string Hub, string User, string Password, string Nic)> Connects { get; } = new();
        public List<string> Disconnects { get; } = new();

        public Task EnsureVirtualAdapterAsync(string nicName, CancellationToken cancellationToken = default)
        {
            EnsuredAdapters.Add(nicName);
            return Task.CompletedTask;
        }

        public Task ConnectAsync(string accountName, string host, int port, string hubName, string userName, string password, string nicName, CancellationToken cancellationToken = default)
        {
            Connects.Add((accountName, host, port, hubName, userName, password, nicName));
            _connectedHost[accountName] = host;
            return Task.CompletedTask;
        }

        public Task<SoftEtherClientSessionStatus> GetStatusAsync(string accountName, CancellationToken cancellationToken = default)
        {
            var established = !NeverEstablish && _connectedHost.TryGetValue(accountName, out var host) && !FailHosts.Contains(host);
            return Task.FromResult(new SoftEtherClientSessionStatus(established, established ? "VPN over UDP with NAT-T (IPv4)" : null, established ? "Session Established" : "Connecting"));
        }

        public Task DisconnectAsync(string accountName, CancellationToken cancellationToken = default)
        {
            Disconnects.Add(accountName);
            _connectedHost.Remove(accountName);
            return Task.CompletedTask;
        }

        public Task<bool> DisableIpBindingsAsync(string nicName, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
