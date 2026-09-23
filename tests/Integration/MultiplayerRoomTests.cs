using System.Collections.Concurrent;
using CODConnect.PacketEngine.Dhcp;
using CODConnect.Protocol;
using CODConnect.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CODConnect.IntegrationTests;

[Collection("rendezvous-env")]
public sealed class MultiplayerRoomTests
{
    private sealed class SimulatedHub
    {
        private readonly object _gate = new();
        private readonly List<HubPort> _ports = [];

        public HubPort Attach(string name)
        {
            var port = new HubPort(this, name);
            lock (_gate) _ports.Add(port);
            return port;
        }

        public int Count { get { lock (_gate) return _ports.Count; } }

        public void Detach(HubPort port) { lock (_gate) _ports.Remove(port); }

        public void Deliver(HubPort from, byte[] frame)
        {
            HubPort[] others;
            lock (_gate) others = _ports.Where(p => p != from).ToArray();
            foreach (var port in others) port.Enqueue(frame);
        }
    }

    private sealed class HubPort(SimulatedHub hub, string name) : IConsoleNetworkInterface, IPeerAwarePort
    {
        private readonly ConcurrentQueue<CapturedFrame> _inbound = new();
        private volatile bool _attached = true;

        public string Name => name;
        public LinkState TransportLinkState => _attached ? LinkState.Up : LinkState.Down;
        public MacAddress GetMac() => MacAddress.None;
        public int GetMtu() => 1500;
        public LinkState GetLinkState() => _attached && hub.Count >= 2 ? LinkState.Up : LinkState.Down;
        public void Enqueue(byte[] frame) => _inbound.Enqueue(new CapturedFrame(frame, DateTimeOffset.UtcNow));
        public CapturedFrame? CaptureFrame(CancellationToken cancellationToken = default) => _inbound.TryDequeue(out var frame) ? frame : null;

        public ValueTask InjectFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            if (_attached) hub.Deliver(this, frame.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _attached = false;
            hub.Detach(this);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HubTransport(SimulatedHub hub, string kind = "Direct") : IRoomTransport
    {
        public string ConnectionKind => kind;

        public Task<(IConsoleNetworkInterface Port, EndpointInfo Advertise)> HostAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<(IConsoleNetworkInterface, EndpointInfo)>((hub.Attach("host"), new EndpointInfo(["sim"], 1, "SIMHUB", "room", "pw")));

        public Task<IConsoleNetworkInterface> JoinAsync(IReadOnlyList<EndpointInfo> peers, CancellationToken cancellationToken = default)
            => Task.FromResult<IConsoleNetworkInterface>(hub.Attach("joiner"));
    }

    private sealed class HangableHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public volatile bool Hang;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed record Pc(string Name, RoomSession Session, VirtualPairLink Console, MacAddress ConsoleMac, HangableHandler Rendezvous);

    private static MacAddress ConsoleMac(int i) => new(0x02, 0xC0, 0x00, 0x00, 0x00, (byte)(0x10 + i));

    private static Task<Pc> StartPcAsync(WebApplicationFactory<Program> factory, SimulatedHub hub, int i, string name, string? roomCode, string kind = "Direct")
        => StartPcAsync(() => factory.Server.CreateHandler(), factory.Server.BaseAddress, hub, i, name, roomCode, kind);

    /// <summary>Sends every request to whichever room server is current, so a test can restart it.</summary>
    private sealed class CurrentServerHandler(Func<HttpMessageHandler> current) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => new HttpMessageInvoker(current(), disposeHandler: false).SendAsync(request, cancellationToken);
    }

    private static async Task<Pc> StartPcAsync(Func<HttpMessageHandler> server, Uri baseAddress, SimulatedHub hub, int i, string name, string? roomCode, string kind = "Direct")
    {
        var mac = ConsoleMac(i);
        var (console, engine) = VirtualPairLink.CreatePair($"console-{i}", $"engine-{i}", mac, MacAddress.None);
        var handler = new HangableHandler(new CurrentServerHandler(server));
        var options = new RoomSessionOptions
        {
            DisplayName = name,
            RoomCode = roomCode,
            Rendezvous = new HttpClient(handler) { BaseAddress = baseAddress },
            Transport = new HubTransport(hub, kind),
            ConsoleFactory = () => Task.FromResult<IConsoleNetworkInterface>(engine),
            LeaveTimeout = TimeSpan.FromSeconds(1),
            RoomWatchdogInterval = TimeSpan.FromSeconds(1),
        };
        var session = roomCode is null ? await RoomSession.HostAsync(options) : await RoomSession.JoinAsync(options);
        return new Pc(name, session, console, mac, handler);
    }

    private static ValueTask AnnounceAsync(Pc pc, int i)
    {
        var payload = DhcpPacket.BuildPayload(
            op: 1, messageType: DhcpMessageType.Discover, xid: (uint)(0x1000 + i), flags: 0,
            ciaddr: IPv4Address.None, yiaddr: IPv4Address.None, siaddr: IPv4Address.None, giaddr: IPv4Address.None,
            clientMac: pc.ConsoleMac, hostname: $"PS5-{i}", requestedIp: null, serverIdentifier: null,
            subnetMask: null, leaseSeconds: 0, broadcastAddress: null);
        var udp = UdpPacket.BuildPayload(UdpPacket.PortDhcpClient, UdpPacket.PortDhcpServer, payload, IPv4Address.None, IPv4Address.Broadcast, computeChecksum: false);
        var ip = IPv4Packet.BuildPayload(IPv4Address.None, IPv4Address.Broadcast, IPv4Packet.ProtocolUdp, udp);
        return pc.Console.InjectFrameAsync(EthernetFrame.Build(MacAddress.Broadcast, pc.ConsoleMac, EthernetFrame.EtherTypeIpv4, ip));
    }

    private static async Task WaitAsync(Func<bool> done, Func<string> detail, int seconds = 20)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (!done() && DateTimeOffset.UtcNow < until) await Task.Delay(100);
        Assert.True(done(), detail());
    }

    private static string Describe(Pc pc)
        => $"{pc.Name}: " + string.Join(", ", pc.Session.Players.Select(p => $"{p.Name}[{(p.Connected ? "on" : "off")},{p.Console ?? "-"},{(p.ConsoleReady ? "ready" : "not")}{(p.Relay ? ",relay" : "")}]"));

    [Fact]
    public async Task SixPlayers_EveryoneSeesEveryone_ThenOneLeavesAndOneCrashes()
    {
        using var factory = new WebApplicationFactory<Program>();
        var hub = new SimulatedHub();
        string[] names = ["Host", "Kyle", "Jackson", "Ana", "Mo", "Lee"];
        var pcs = new List<Pc> { await StartPcAsync(factory, hub, 0, names[0], null) };
        for (var i = 1; i < names.Length; i++)
        {
            pcs.Add(await StartPcAsync(factory, hub, i, names[i], pcs[0].Session.RoomCode, kind: i == 2 ? "Relay" : "Direct"));
        }

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => StartPcAsync(factory, hub, 6, "Seventh", pcs[0].Session.RoomCode));

            for (var i = 0; i < pcs.Count; i++) await AnnounceAsync(pcs[i], i);

            foreach (var pc in pcs)
            {
                await WaitAsync(
                    () => pc.Session.Players.Count == 5 && pc.Session.Players.All(p => p.Connected && p.ConsoleReady && p.Console == "PS5"),
                    () => Describe(pc));
                Assert.Equal(names.Where(n => n != pc.Name), pc.Session.Players.Select(p => p.Name));
            }

            Assert.All(pcs.Where(p => p.Name != "Jackson"), pc => Assert.True(pc.Session.Players.Single(p => p.Name == "Jackson").Relay));

            var leased = pcs.Select(pc => pcs[0].Session.Dhcp!.Leases.TryGet(pc.ConsoleMac, out var address) ? address : IPv4Address.None).ToList();
            Assert.DoesNotContain(IPv4Address.None, leased);
            Assert.Equal(6, leased.Distinct().Count());

            await pcs[3].Session.DisposeAsync();
            foreach (var pc in pcs.Where(p => p.Name != "Ana"))
            {
                await WaitAsync(() => pc.Session.Players.Count == 4 && pc.Session.Players.All(p => p.Name != "Ana"), () => Describe(pc));
            }

            pcs[4].Rendezvous.Hang = true;
            await pcs[4].Session.DisposeAsync();
            foreach (var pc in pcs.Where(p => p.Name is not ("Ana" or "Mo")))
            {
                await WaitAsync(() => pc.Session.Players.Single(p => p.Name == "Mo").Connected == false, () => Describe(pc));
            }

            var arp = ArpPacket.BuildEthernetFrame(pcs[1].ConsoleMac, new IPv4Address(10, 42, 0, 50), new IPv4Address(10, 42, 0, 51), isReply: false, replyDestinationMac: MacAddress.None);
            while (pcs[5].Console.CaptureFrame() is not null) { }
            await pcs[1].Console.InjectFrameAsync(arp);
            var seen = new List<byte[]>();
            await WaitAsync(() => { seen.AddRange(Drain(pcs[5].Console)); return seen.Any(f => f.AsSpan().SequenceEqual(arp)); },
                () => $"ARP from Kyle's console never reached Lee's; Lee got {seen.Count} frames; Kyle counters {pcs[1].Session.Counters.Snapshot()}; Lee counters {pcs[5].Session.Counters.Snapshot()}; hub ports {hub.Count}");
        }
        finally
        {
            foreach (var pc in pcs) await pc.Session.DisposeAsync();
        }
    }

    private static List<byte[]> Drain(VirtualPairLink console)
    {
        var frames = new List<byte[]>();
        while (console.CaptureFrame() is { } frame) frames.Add(frame.Buffer.ToArray());
        return frames;
    }

    [Fact]
    public async Task HostLeaving_EndsTheRoomForEveryFriend_AndNamesAreUnique()
    {
        using var factory = new WebApplicationFactory<Program>();
        var hub = new SimulatedHub();
        var host = await StartPcAsync(factory, hub, 0, "Host", null);
        var kyle = await StartPcAsync(factory, hub, 1, "Kyle", host.Session.RoomCode);
        var ana = await StartPcAsync(factory, hub, 2, "Ana", host.Session.RoomCode);
        await using var _ = kyle.Session;
        await using var __ = ana.Session;

        var twin = await Assert.ThrowsAsync<InvalidOperationException>(() => StartPcAsync(factory, hub, 3, "kyle", host.Session.RoomCode));
        Assert.Contains("already called", twin.Message);

        var ended = new ConcurrentBag<string>();
        kyle.Session.Ended += reason => ended.Add("Kyle: " + reason);
        ana.Session.Ended += reason => ended.Add("Ana: " + reason);

        await host.Session.DisposeAsync();

        await WaitAsync(() => ended.Count == 2, () => string.Join("; ", ended), seconds: 15);
        Assert.All(ended, reason => Assert.Contains("closed the room", reason));
    }

    /// <summary>
    /// The room server restarts and forgets every room (it keeps them in memory): the host puts
    /// the room back under the same code, friends rejoin as themselves, nobody is told the room
    /// ended, and afterwards the host closing the room still ends it for everyone.
    /// </summary>
    [Fact]
    public async Task RoomServerRestart_TheRoomComesBackOnItsOwn()
    {
        var server = new WebApplicationFactory<Program>();
        var baseAddress = server.Server.BaseAddress;
        HttpMessageHandler Current() => server.Server.CreateHandler();
        var hub = new SimulatedHub();
        var host = await StartPcAsync(Current, baseAddress, hub, 0, "Host", null);
        var kyle = await StartPcAsync(Current, baseAddress, hub, 1, "Kyle", host.Session.RoomCode);
        var ana = await StartPcAsync(Current, baseAddress, hub, 2, "Ana", host.Session.RoomCode);
        var pcs = new[] { host, kyle, ana };
        var ended = new ConcurrentBag<string>();
        foreach (var pc in pcs) pc.Session.Ended += reason => ended.Add($"{pc.Name}: {reason}");

        try
        {
            foreach (var pc in pcs) await WaitAsync(() => pc.Session.Players.Count == 2 && pc.Session.Players.All(p => p.Connected), () => Describe(pc));

            var old = server;
            server = new WebApplicationFactory<Program>();
            old.Dispose();

            var rendezvous = new RendezvousClient(new HttpClient(new CurrentServerHandler(Current)) { BaseAddress = baseAddress });
            await WaitAsync(() => rendezvous.GetRoomAsync(host.Session.RoomCode).GetAwaiter().GetResult()?.MemberCount == 3,
                () => $"room not back: {rendezvous.GetRoomAsync(host.Session.RoomCode).GetAwaiter().GetResult()?.MemberCount}", seconds: 30);
            Assert.Empty(ended);
            foreach (var pc in pcs) Assert.True(pc.Session.Players.All(p => p.Connected), Describe(pc));

            await host.Session.DisposeAsync();
            await WaitAsync(() => ended.Count == 2, () => string.Join("; ", ended), seconds: 15);
            Assert.All(ended, reason => Assert.Contains("closed the room", reason));
        }
        finally
        {
            foreach (var pc in pcs) await pc.Session.DisposeAsync();
            server.Dispose();
        }
    }
}
