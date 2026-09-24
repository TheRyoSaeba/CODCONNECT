using CODConnect.NetworkSimulation;
using CODConnect.PacketEngine;
using CODConnect.Protocol;
using CODConnect.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CODConnect.IntegrationTests;

public class PcInternetTests
{
    private static readonly MacAddress ConsoleMac = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x0A);
    private static readonly MacAddress PcMac = new(0x02, 0x00, 0x00, 0x00, 0x00, 0xFE);
    private static readonly MacAddress SecureNatMac = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x7E);
    private static readonly IPv4Address Console = new(10, 42, 0, 2);
    private static readonly IPv4Address Sony = new(23, 60, 39, 133);
    private static readonly IPv4Address Room = new(10, 42, 0, 1);
    private static readonly IPv4Address Mask = new(255, 255, 255, 0);
    private const byte Syn = 0x02, Ack = 0x10, Rst = 0x04;

    private static byte[] Tcp(IPv4Address destination, byte flags)
    {
        var tcp = new byte[20];
        tcp[0] = 0xD8; tcp[1] = 0x5B; tcp[3] = 80; tcp[12] = 0x50; tcp[13] = flags;
        return EthernetFrame.Build(PcMac, ConsoleMac, EthernetFrame.EtherTypeIpv4, IPv4Packet.BuildPayload(Console, destination, IPv4Packet.ProtocolTcp, tcp));
    }

    [Fact]
    public void HandshakeToTheInternet_ConfirmsThePcCarriesTheConsole()
    {
        var watch = new PcInternetWatch(Room, Mask, TimeSpan.FromSeconds(60));

        watch.ObserveConsoleFrame(Tcp(Sony, Syn));
        Assert.False(watch.Confirmed);
        watch.ObserveConsoleFrame(Tcp(Sony, Ack));

        Assert.True(watch.Confirmed);
        Assert.False(watch.GaveUp);
    }

    [Fact]
    public void OnlyUnansweredAttempts_GiveUpAfterThePatience()
    {
        var now = DateTimeOffset.UtcNow;
        var watch = new PcInternetWatch(Room, Mask, TimeSpan.FromSeconds(60), () => now);

        watch.ObserveConsoleFrame(Tcp(Sony, Syn));
        now += TimeSpan.FromSeconds(30);
        watch.ObserveConsoleFrame(Tcp(Sony, Syn));
        watch.ObserveConsoleFrame(Tcp(Sony, Rst | Ack));
        Assert.False(watch.GaveUp);

        now += TimeSpan.FromSeconds(31);
        Assert.True(watch.GaveUp);
        Assert.False(watch.Confirmed);
    }

    [Fact]
    public void RoomAndMulticastTraffic_SaysNothingAboutTheInternet()
    {
        var now = DateTimeOffset.UtcNow;
        var watch = new PcInternetWatch(Room, Mask, TimeSpan.FromSeconds(1), () => now);

        foreach (var destination in new[] { new IPv4Address(10, 42, 0, 3), new IPv4Address(239, 255, 255, 250), IPv4Address.Broadcast })
        {
            watch.ObserveConsoleFrame(Tcp(destination, Syn));
            watch.ObserveConsoleFrame(Tcp(destination, Ack));
        }

        now += TimeSpan.FromMinutes(5);
        Assert.False(watch.Confirmed);
        Assert.False(watch.GaveUp);
    }

    [Fact]
    public async Task WifiRoom_UsesWindowsSharing_AndNeverStartsTheSoftEtherGateway()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (consoleSim, engineConsole) = VirtualPairLink.CreatePair("console", "engine", ConsoleMac, MacAddress.None);
        await using var _ = consoleSim;
        var transport = new GatewayTransport(new RecordingPort("gateway", SecureNatMac));
        var released = 0;

        await using var host = await RoomSession.HostAsync(new RoomSessionOptions
        {
            DisplayName = "HostA",
            Rendezvous = factory.CreateClient(),
            Transport = transport,
            ConsoleFactory = () => Task.FromResult<IConsoleNetworkInterface>(engineConsole),
            ReleasePcGateway = () => { Interlocked.Increment(ref released); return Task.CompletedTask; },
        });

        Assert.Equal("Waiting", host.ConsoleInternet);
        await consoleSim.InjectFrameAsync(Tcp(Sony, Syn));
        await consoleSim.InjectFrameAsync(Tcp(Sony, Ack));

        Assert.True(await Eventually(() => host.ConsoleInternet == "Ready"));
        Assert.Null(transport.Requested);
        Assert.Equal(0, released);
    }

    [Fact]
    public async Task WifiRoom_WhenWindowsDoesNotCarryTheConsole_FallsBackAndPointsTheConsoleAtTheGateway()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (consoleSim, engineConsole) = VirtualPairLink.CreatePair("console", "engine", ConsoleMac, MacAddress.None);
        await using var _ = consoleSim;
        var gateway = new RecordingPort("gateway", new MacAddress(0x02, 0x00, 0x00, 0x00, 0x00, 0x08));
        var transport = new GatewayTransport(gateway);
        var released = 0;

        await using var host = await RoomSession.HostAsync(new RoomSessionOptions
        {
            DisplayName = "HostA",
            Rendezvous = factory.CreateClient(),
            Transport = transport,
            ConsoleFactory = () => Task.FromResult<IConsoleNetworkInterface>(engineConsole),
            ReleasePcGateway = () => { Interlocked.Increment(ref released); return Task.CompletedTask; },
            PcInternetPatience = TimeSpan.FromMilliseconds(300),
        });

        await consoleSim.InjectFrameAsync(Tcp(Sony, Syn));
        Assert.True(await Eventually(() => host.ConsoleInternet == "Ready" && transport.Requested is not null));
        Assert.Equal(1, released);

        var gatewayAddress = new IPv4Address(10, 42, 0, 254);
        gateway.EnqueueFrame(ArpPacket.BuildEthernetFrame(SecureNatMac, gatewayAddress, new IPv4Address(10, 42, 0, 255), isReply: false, MacAddress.None));

        Assert.True(await Eventually(() =>
        {
            while (consoleSim.CaptureFrame() is { } frame)
            {
                if (EthernetFrame.TryParse(frame.Buffer.Span, out var parsed)
                    && parsed.EtherType == EthernetFrame.EtherTypeArp
                    && ArpPacket.TryParse(parsed.Payload, out var arp)
                    && arp.SenderIp == gatewayAddress && arp.TargetIp == gatewayAddress
                    && parsed.Source == SecureNatMac)
                {
                    return true;
                }
            }

            return false;
        }));
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private sealed class GatewayTransport(IConsoleNetworkInterface gateway) : IRoomTransport, IConsoleInternetProvider
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
}
