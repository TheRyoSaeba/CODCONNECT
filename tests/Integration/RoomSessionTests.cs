using CODConnect.Rendezvous;
using Microsoft.Extensions.DependencyInjection;
using CODConnect.Core.Diagnostics;
using CODConnect.NetworkSimulation;
using CODConnect.PacketEngine.Dhcp;
using CODConnect.Protocol;
using CODConnect.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CODConnect.IntegrationTests;

[Collection("rendezvous-env")]
public class RoomSessionTests
{
    private static readonly MacAddress HostConsoleMac = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x0A);
    private static readonly MacAddress JoinerConsoleMac = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x0B);

    private static async Task<T?> WaitForAsync<T>(Func<T?> probe, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = probe();
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(5);
        }

        return default;
    }

    [Fact]
    public async Task HostAndJoin_RoomSession_ExchangesArpAndDhcpLease()
    {
        using var factory = new WebApplicationFactory<Program>();

        var (hostConsoleSim, hostEngineConsole) = VirtualPairLink.CreatePair("host-console", "host-engine", HostConsoleMac, MacAddress.None);
        var (joinerConsoleSim, joinerEngineConsole) = VirtualPairLink.CreatePair("joiner-console", "joiner-engine", JoinerConsoleMac, MacAddress.None);

        await using var hostSession = await RoomSession.HostAsync(new RoomSessionOptions
        {
            DisplayName = "HostA",
            Rendezvous = factory.CreateClient(),
            Transport = new TcpRoomTransport { ListenPort = 0 },
            ConsoleFactory = () => Task.FromResult<IConsoleNetworkInterface>(hostEngineConsole),
        });

        await using var joinerSession = await RoomSession.JoinAsync(new RoomSessionOptions
        {
            DisplayName = "PlayerB",
            RoomCode = hostSession.RoomCode,
            Rendezvous = factory.CreateClient(),
            Transport = new TcpRoomTransport { ListenPort = 0 },
            ConsoleFactory = () => Task.FromResult<IConsoleNetworkInterface>(joinerEngineConsole),
        });

        Assert.Equal(RoomRole.Host, hostSession.Role);
        Assert.Equal(RoomRole.Joiner, joinerSession.Role);
        Assert.Equal(TunnelState.Connected, hostSession.TunnelState);
        Assert.Equal(TunnelState.Connected, joinerSession.TunnelState);
        Assert.NotNull(hostSession.AdminKey);

        var arpRequest = ArpPacket.BuildEthernetFrame(JoinerConsoleMac, new IPv4Address(10, 42, 0, 11), new IPv4Address(10, 42, 0, 12), isReply: false, replyDestinationMac: MacAddress.None);
        await joinerConsoleSim.InjectFrameAsync(arpRequest);

        var receivedOnHost = await WaitForAsync(() => hostConsoleSim.CaptureFrame());
        Assert.NotNull(receivedOnHost);
        Assert.True(receivedOnHost!.Value.Buffer.Span.SequenceEqual(arpRequest));

        var arpReply = ArpPacket.BuildEthernetFrame(HostConsoleMac, new IPv4Address(10, 42, 0, 12), new IPv4Address(10, 42, 0, 11), isReply: true, replyDestinationMac: JoinerConsoleMac);
        await hostConsoleSim.InjectFrameAsync(arpReply);

        var receivedOnJoiner = await WaitForAsync(() => joinerConsoleSim.CaptureFrame());
        Assert.NotNull(receivedOnJoiner);
        Assert.True(receivedOnJoiner!.Value.Buffer.Span.SequenceEqual(arpReply));

        var discover = BuildDhcpFrame(JoinerConsoleMac, DhcpMessageType.Discover, DhcpPacket.FlagBroadcast, hostname: "PS5");
        await joinerConsoleSim.InjectFrameAsync(discover);

        var offer = await WaitForAsync(() => FindDhcpReply(joinerConsoleSim, DhcpMessageType.Offer));
        Assert.NotNull(offer);
        Assert.Contains("yiaddr=10.42.0.2", offer);

        var request = BuildDhcpFrame(JoinerConsoleMac, DhcpMessageType.Request, DhcpPacket.FlagBroadcast, requested: new IPv4Address(10, 42, 0, 2));
        await joinerConsoleSim.InjectFrameAsync(request);

        var ack = await WaitForAsync(() => FindDhcpReply(joinerConsoleSim, DhcpMessageType.Ack));
        Assert.NotNull(ack);
        Assert.Contains("yiaddr=10.42.0.2", ack);

        Assert.True(hostSession.Dhcp!.Leases.TryGet(JoinerConsoleMac, out var leased));
        Assert.Equal(new IPv4Address(10, 42, 0, 2), leased);

        Assert.True(hostSession.LocalConsoleReady);
        Assert.True(hostSession.RemoteConsoleReady);
        Assert.True(joinerSession.LocalConsoleReady);
        Assert.True(joinerSession.RemoteConsoleReady);

        var hostView = hostSession.Devices.Single(d => d.Mac == JoinerConsoleMac);
        Assert.Equal(DeviceSide.Remote, hostView.Side);
        Assert.Equal("PS5", hostView.Identity);
        Assert.Equal("PS5", hostView.Hostname);
        Assert.Equal(new IPv4Address(10, 42, 0, 2), hostView.Ip);

        var joinerView = joinerSession.Devices.Single(d => d.Mac == JoinerConsoleMac);
        Assert.Equal(DeviceSide.Local, joinerView.Side);
        Assert.Equal("PS5", joinerView.Identity);
        Assert.Equal(new IPv4Address(10, 42, 0, 2), joinerView.Ip);

        Assert.Equal(DeviceSide.Remote, joinerSession.Devices.Single(d => d.Mac == HostConsoleMac).Side);
        Assert.DoesNotContain(joinerSession.Devices, d => d.Mac == joinerSession.DataPort.GetMac());
        Assert.Equal("Direct", hostSession.ConnectionKind);

        var rooms = factory.Services.GetRequiredService<RoomStore>();
        var info = rooms.Get(hostSession.RoomCode);
        Assert.NotNull(info);
        Assert.Equal(2, info!.MemberCount);

        await hostSession.DisposeAsync();
        Assert.Null(rooms.Get(hostSession.RoomCode));

        Assert.Equal(0, hostSession.Counters.FramesDropped);
        Assert.Equal(0, joinerSession.Counters.FramesDropped);
    }

    [Fact]
    public async Task Session_RoomDisappears_EndsWithReason()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (consoleSim, engineConsole) = VirtualPairLink.CreatePair("host-console", "host-engine", HostConsoleMac, MacAddress.None);
        await using var _ = consoleSim;

        await using var session = await RoomSession.HostAsync(new RoomSessionOptions
        {
            DisplayName = "HostA",
            Rendezvous = factory.CreateClient(),
            Transport = new TcpRoomTransport { ListenPort = 0 },
            ConsoleFactory = () => Task.FromResult<IConsoleNetworkInterface>(engineConsole),
            RoomWatchdogInterval = TimeSpan.FromMilliseconds(100),
        });

        var ended = new TaskCompletionSource<string>();
        session.Ended += reason => ended.TrySetResult(reason);

        Assert.True(await new RendezvousClient(factory.CreateClient()).CloseRoomAsync(session.RoomCode, session.AdminKey!));

        var reason = await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("room", reason, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class HangableHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public volatile bool Hang;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ThrowingDisposeConsole(IConsoleNetworkInterface inner) : IConsoleNetworkInterface
    {
        public string Name => inner.Name;
        public MacAddress GetMac() => inner.GetMac();
        public int GetMtu() => inner.GetMtu();
        public LinkState GetLinkState() => inner.GetLinkState();
        public CapturedFrame? CaptureFrame(CancellationToken cancellationToken = default) => inner.CaptureFrame(cancellationToken);
        public ValueTask InjectFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default) => inner.InjectFrameAsync(frame, cancellationToken);
        public ValueTask DisposeAsync() => throw new IOException("adapter vanished");
    }

    private static async Task<(RoomSession Host, RoomSession Joiner)> ConnectedPairAsync(
        WebApplicationFactory<Program> factory, HttpClient? hostRendezvous = null, Func<IConsoleNetworkInterface, IConsoleNetworkInterface>? wrapHostConsole = null)
    {
        var (_, hostEngine) = VirtualPairLink.CreatePair("hc", "he", HostConsoleMac, MacAddress.None);
        var (_, joinEngine) = VirtualPairLink.CreatePair("jc", "je", JoinerConsoleMac, MacAddress.None);
        var host = await RoomSession.HostAsync(new RoomSessionOptions
        {
            DisplayName = "HostA",
            Rendezvous = hostRendezvous ?? factory.CreateClient(),
            Transport = new TcpRoomTransport { ListenPort = 0 },
            ConsoleFactory = () => Task.FromResult(wrapHostConsole?.Invoke(hostEngine) ?? hostEngine),
            LeaveTimeout = TimeSpan.FromSeconds(1),
        });
        var joiner = await RoomSession.JoinAsync(new RoomSessionOptions
        {
            DisplayName = "PlayerB",
            RoomCode = host.RoomCode,
            Rendezvous = factory.CreateClient(),
            Transport = new TcpRoomTransport { ListenPort = 0 },
            ConsoleFactory = () => Task.FromResult<IConsoleNetworkInterface>(joinEngine),
        });
        Assert.NotNull(await WaitForAsync(() => host.TunnelState == TunnelState.Connected ? (object)true : null));
        return (host, joiner);
    }

    [Fact]
    public async Task JoinerLeaving_FreesItsSlotAtTheRendezvous()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (host, joiner) = await ConnectedPairAsync(factory);
        await using var _ = host;
        var rooms = factory.Services.GetRequiredService<RoomStore>();
        Assert.Equal(2, rooms.Get(host.RoomCode)!.MemberCount);

        await joiner.DisposeAsync();

        Assert.Equal(1, rooms.Get(host.RoomCode)!.MemberCount);
    }

    [Fact]
    public async Task FailingTeardownStep_StillReleasesTheTunnel()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (host, joiner) = await ConnectedPairAsync(factory, wrapHostConsole: c => new ThrowingDisposeConsole(c));
        await using var __ = joiner;
        var dataPort = host.DataPort;
        Assert.Equal(LinkState.Up, dataPort.GetLinkState());

        await host.DisposeAsync();

        Assert.Equal(LinkState.Down, dataPort.GetLinkState());
        Assert.Null(factory.Services.GetRequiredService<RoomStore>().Get(host.RoomCode));
    }

    [Fact]
    public async Task HungRendezvous_DoesNotStallLeaving()
    {
        using var factory = new WebApplicationFactory<Program>();
        var handler = new HangableHandler(factory.Server.CreateHandler());
        var (host, joiner) = await ConnectedPairAsync(factory, new HttpClient(handler) { BaseAddress = factory.Server.BaseAddress });
        await using var __ = joiner;
        var dataPort = host.DataPort;

        handler.Hang = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await host.DisposeAsync();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"leaving took {clock.Elapsed}");
        Assert.Equal(LinkState.Down, dataPort.GetLinkState());
    }

    [Fact]
    public async Task Join_UnknownRoom_Fails()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (consoleSim, engineConsole) = VirtualPairLink.CreatePair("c", "e", JoinerConsoleMac, MacAddress.None);
        await using var _ = engineConsole;
        await using var __ = consoleSim;

        await Assert.ThrowsAsync<InvalidOperationException>(() => RoomSession.JoinAsync(new RoomSessionOptions
        {
            DisplayName = "PlayerB",
            RoomCode = "ZZZZ-ZZ",
            Rendezvous = factory.CreateClient(),
            Transport = new TcpRoomTransport { ListenPort = 0 },
            ConsoleFactory = () => Task.FromResult<IConsoleNetworkInterface>(engineConsole),
            PeerPollTimeout = TimeSpan.FromSeconds(2),
        }));
    }

    [Fact]
    public async Task ConsoleInternet_GatewayJoinsTheRoom_DhcpAnsweredOnce_RemovedOnLeave()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (consoleSim, engineConsole) = VirtualPairLink.CreatePair("host-console", "host-engine", HostConsoleMac, MacAddress.None);
        await using var __ = consoleSim;
        var gateway = new RecordingPort("gateway", new MacAddress(0x02, 0x00, 0x00, 0x00, 0x00, 0x7E));
        var transport = new InternetTcpTransport(gateway);

        var host = await RoomSession.HostAsync(new RoomSessionOptions
        {
            DisplayName = "HostA",
            Rendezvous = factory.CreateClient(),
            Transport = transport,
            ConsoleFactory = () => Task.FromResult<IConsoleNetworkInterface>(engineConsole),
        });

        Assert.Equal("Ready", await WaitForAsync(() => host.ConsoleInternet == "Ready" ? host.ConsoleInternet : null));
        Assert.Equal((new IPv4Address(10, 42, 0, 254), new IPv4Address(255, 255, 255, 0)), transport.Requested);

        await consoleSim.InjectFrameAsync(BuildDhcpFrame(HostConsoleMac, DhcpMessageType.Discover, DhcpPacket.FlagBroadcast));
        Assert.NotNull(await WaitForAsync(() => gateway.SnapshotInjected().Count > 0 ? "seen" : null));

        var offers = new List<DhcpInfo>();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (consoleSim.CaptureFrame() is { } frame && TryParseDhcpReply(frame.Buffer.Span, out var reply) && reply.MessageType == DhcpMessageType.Offer)
            {
                offers.Add(reply);
            }

            await Task.Delay(5);
        }

        var offer = Assert.Single(offers);
        Assert.Equal(new IPv4Address(10, 42, 0, 254), offer.Router);
        Assert.Equal(new IPv4Address(1, 1, 1, 1), offer.DnsServer);
        Assert.DoesNotContain(host.Devices, d => d.Mac == gateway.Mac);

        await host.DisposeAsync();
        Assert.Equal(LinkState.Down, gateway.GetLinkState());
    }

    private static bool TryParseDhcpReply(ReadOnlySpan<byte> frame, out DhcpInfo dhcp)
    {
        dhcp = new DhcpInfo();
        return EthernetFrame.TryParse(frame, out var parsed)
               && parsed.EtherType == EthernetFrame.EtherTypeIpv4
               && IPv4Packet.TryParse(parsed.Payload, out var ip)
               && ip.Protocol == IPv4Packet.ProtocolUdp
               && UdpPacket.TryParse(ip.Payload, out var udp)
               && udp.SourcePort == UdpPacket.PortDhcpServer
               && DhcpPacket.TryParse(udp.Payload, out dhcp);
    }

    private sealed class InternetTcpTransport(IConsoleNetworkInterface gateway) : IRoomTransport, IConsoleInternetProvider
    {
        private readonly TcpRoomTransport _inner = new() { ListenPort = 0 };

        public (IPv4Address Gateway, IPv4Address Mask)? Requested { get; private set; }

        public string ConnectionKind => _inner.ConnectionKind;

        public Task<(IConsoleNetworkInterface Port, EndpointInfo Advertise)> HostAsync(CancellationToken cancellationToken = default) => _inner.HostAsync(cancellationToken);

        public Task<IConsoleNetworkInterface> JoinAsync(IReadOnlyList<EndpointInfo> peers, CancellationToken cancellationToken = default) => _inner.JoinAsync(peers, cancellationToken);

        public Task<IConsoleNetworkInterface> StartConsoleInternetAsync(IPv4Address gatewayAddress, IPv4Address mask, CancellationToken cancellationToken = default)
        {
            Requested = (gatewayAddress, mask);
            return Task.FromResult(gateway);
        }
    }

    private static byte[] BuildDhcpFrame(MacAddress clientMac, DhcpMessageType type, ushort flags, IPv4Address? requested = null, string? hostname = null)
    {
        var payload = DhcpPacket.BuildPayload(
            op: 1, messageType: type, xid: 0x44556677, flags: flags,
            ciaddr: IPv4Address.None, yiaddr: IPv4Address.None,
            siaddr: IPv4Address.None, giaddr: IPv4Address.None,
            clientMac: clientMac, hostname: hostname,
            requestedIp: requested, serverIdentifier: new IPv4Address(10, 42, 0, 1),
            subnetMask: null, leaseSeconds: 0, broadcastAddress: null);
        var udp = UdpPacket.BuildPayload(UdpPacket.PortDhcpClient, UdpPacket.PortDhcpServer, payload, IPv4Address.None, IPv4Address.Broadcast, computeChecksum: false);
        var ipPacket = IPv4Packet.BuildPayload(IPv4Address.None, IPv4Address.Broadcast, IPv4Packet.ProtocolUdp, udp);
        return EthernetFrame.Build(MacAddress.Broadcast, clientMac, EthernetFrame.EtherTypeIpv4, ipPacket);
    }

    private static string? FindDhcpReply(VirtualPairLink console, DhcpMessageType type)
    {
        var frame = console.CaptureFrame();
        while (frame is not null)
        {
            var description = DescribeDhcp(frame.Value.Buffer.Span);
            if (description.StartsWith(type.ToString()))
            {
                return description;
            }

            frame = console.CaptureFrame();
        }

        return null;
    }

    private static string DescribeDhcp(ReadOnlySpan<byte> frame)
    {
        if (!EthernetFrame.TryParse(frame, out var parsed)
            || parsed.EtherType != EthernetFrame.EtherTypeIpv4
            || !IPv4Packet.TryParse(parsed.Payload, out var ip)
            || ip.Protocol != IPv4Packet.ProtocolUdp
            || !UdpPacket.TryParse(ip.Payload, out var udp)
            || udp.SourcePort != UdpPacket.PortDhcpServer
            || !DhcpPacket.TryParse(udp.Payload, out var dhcp))
        {
            return "not-dhcp";
        }

        return $"{dhcp.MessageType} yiaddr={dhcp.Yiaddr}";
    }
}
