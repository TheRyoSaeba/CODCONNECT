using CODConnect.Rendezvous;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using CODConnect.Core.Diagnostics;
using CODConnect.NetworkSimulation;
using CODConnect.Protocol;
using CODConnect.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CODConnect.IntegrationTests;

public class ReconnectTests
{
    private static readonly MacAddress HostConsoleMac = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x0A);
    private static readonly MacAddress JoinerConsoleMac = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x0B);

    private sealed class MutablePort : IConsoleNetworkInterface
    {
        private readonly ConcurrentQueue<CapturedFrame> _inbound = new();
        private ConcurrentQueue<CapturedFrame>? _peerInbound;

        public string Name { get; }
        public LinkState State { get; set; } = LinkState.Up;
        public int CreatedAt;

        public bool LoopbackInject { get; init; }

        public MutablePort(string name, int createdAt) { Name = name; CreatedAt = createdAt; }

        public static (MutablePort Console, MutablePort Engine) CreatePair(string name, int createdAt, MacAddress consoleMac)
        {
            var consoleSide = new MutablePort($"{name}-console", createdAt);
            var engineSide = new MutablePort($"{name}-engine", createdAt);
            consoleSide._peerInbound = engineSide._inbound;
            engineSide._peerInbound = consoleSide._inbound;
            consoleSide.State = LinkState.Up;
            return (consoleSide, engineSide);
        }

        public MacAddress GetMac() => MacAddress.None;
        public int GetMtu() => 1500;
        public LinkState GetLinkState() => State;
        public CapturedFrame? CaptureFrame(CancellationToken cancellationToken = default)
            => _inbound.TryDequeue(out var frame) ? frame : null;

        public ValueTask InjectFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            if (State != LinkState.Up)
            {
                return ValueTask.CompletedTask;
            }

            if (LoopbackInject)
            {
                _inbound.Enqueue(new CapturedFrame(frame.ToArray(), DateTimeOffset.UtcNow));
            }
            else if (_peerInbound is not null)
            {
                _peerInbound.Enqueue(new CapturedFrame(frame.ToArray(), DateTimeOffset.UtcNow));
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTransport : IRoomTransport
    {
        private int _createdAt;
        public string ConnectionKind => "Direct";
        public int HostCalls { get; private set; }
        public int JoinCalls { get; private set; }
        public List<MutablePort> HostPorts { get; } = new();
        public List<MutablePort> JoinPorts { get; } = new();

        public Task<(IConsoleNetworkInterface Port, EndpointInfo Advertise)> HostAsync(CancellationToken cancellationToken = default)
        {
            HostCalls++;
            var (console, engine) = MutablePort.CreatePair($"host{HostCalls}", _createdAt++, MacAddress.None);
            var tunnel = new MutablePort($"host{HostCalls}-tunnel", _createdAt++) { LoopbackInject = true };
            HostPorts.Add(tunnel);
            return Task.FromResult<(IConsoleNetworkInterface, EndpointInfo)>(
                (tunnel, new EndpointInfo(["192.168.1.10"], 40000 + HostCalls)));
        }

        public Task<IConsoleNetworkInterface> JoinAsync(IReadOnlyList<EndpointInfo> peers, CancellationToken cancellationToken = default)
        {
            JoinCalls++;
            var (console, engine) = MutablePort.CreatePair($"join{JoinCalls}", _createdAt++, MacAddress.None);
            JoinPorts.Add(engine);
            return Task.FromResult<IConsoleNetworkInterface>(engine);
        }
    }

    private static async Task<T?> WaitForAsync<T>(Func<T?> probe, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = probe();
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(10);
        }

        return default;
    }

    private static async Task<bool> WaitForTrueAsync(Func<bool> probe, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (probe())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return false;
    }

    [Fact]
    public async Task Rendezvous_UpdateEndpoint_ReplacesAdvertisedEndpoint()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = new RendezvousClient(factory.CreateClient());
        var created = await client.CreateRoomAsync(new EndpointInfo(["10.0.0.1"], 40000), "host");

        var updated = await client.UpdateEndpointAsync(created.RoomCode, created.MemberId, created.MemberSecret, new EndpointInfo(["10.0.0.9"], 49999));
        Assert.True(updated);

        var info = await client.GetRoomAsync(created.RoomCode, created.MemberId, created.MemberSecret);
        var host = info!.Members.Single(m => m.MemberId == created.MemberId);
        Assert.Equal("10.0.0.9", host.Endpoint!.Addresses[0]);
        Assert.Equal(49999, host.Endpoint.Port);

        var rejected = await client.UpdateEndpointAsync(created.RoomCode, created.MemberId, "wrong-secret", new EndpointInfo(["10.0.0.8"], 40000));
        Assert.False(rejected);
        var after = await client.GetRoomAsync(created.RoomCode, created.MemberId, created.MemberSecret);
        Assert.Equal("10.0.0.9", after!.Members.Single(m => m.MemberId == created.MemberId).Endpoint!.Addresses[0]);
    }

    [Fact]
    public async Task Session_HostLinkDrops_IsReestablished_AndRendezvousUpdated()
    {
        using var factory = new WebApplicationFactory<Program>();
        var transport = new FakeTransport();
        var (consoleSim, engineConsole) = VirtualPairLink.CreatePair("host-console", "host-engine", HostConsoleMac, MacAddress.None);
        await using var _ = consoleSim;

        await using var session = await RoomSession.HostAsync(new RoomSessionOptions
        {
            DisplayName = "HostA",
            Rendezvous = factory.CreateClient(),
            Transport = transport,
            ConsoleFactory = () => Task.FromResult<IConsoleNetworkInterface>(engineConsole),
            ReconnectGracePeriod = TimeSpan.Zero,
            ReconnectAttemptInterval = TimeSpan.FromMilliseconds(100),
        });

        Assert.Equal(1, transport.HostCalls);
        var originalCode = session.RoomCode;

        transport.HostPorts[^1].State = LinkState.Down;

        Assert.True(await WaitForTrueAsync(() => transport.HostCalls >= 2));
        Assert.True(await WaitForAsync(() =>
        {
            return factory.Services.GetRequiredService<RoomStore>().Get(originalCode) is { } info
                && info.Members.Single(m => m.MemberId == session.MemberId).Endpoint!.Port
                    == 40000 + transport.HostCalls;
        }));

        Assert.Equal(originalCode, session.RoomCode);
    }

    [Fact]
    public async Task Session_SwappedPort_StillCarriesFrames()
    {
        using var factory = new WebApplicationFactory<Program>();
        var transport = new FakeTransport();
        var (consoleSim, engineConsole) = VirtualPairLink.CreatePair("host-console", "host-engine", HostConsoleMac, MacAddress.None);
        await using var session = await RoomSession.HostAsync(new RoomSessionOptions
        {
            DisplayName = "HostA",
            Rendezvous = factory.CreateClient(),
            Transport = transport,
            ConsoleFactory = () => Task.FromResult<IConsoleNetworkInterface>(engineConsole),
            ReconnectGracePeriod = TimeSpan.Zero,
            ReconnectAttemptInterval = TimeSpan.FromMilliseconds(100),
        });

        var oldTunnel = transport.HostPorts[^1];
        oldTunnel.State = LinkState.Down;
        Assert.True(await WaitForTrueAsync(() => transport.HostCalls >= 2));

        var newPair = transport.HostPorts[^1];
        var arp = ArpPacket.BuildEthernetFrame(JoinerConsoleMac, new IPv4Address(10, 42, 0, 5), new IPv4Address(10, 42, 0, 6), isReply: false, replyDestinationMac: MacAddress.None);
        await newPair.InjectFrameAsync(arp);
        var received = await WaitForAsync(() => consoleSim.CaptureFrame());
        Assert.NotNull(received);
        Assert.True(received!.Value.Buffer.Span.SequenceEqual(arp));

        await oldTunnel.InjectFrameAsync(arp);
        Assert.Null(consoleSim.CaptureFrame());
    }
}
