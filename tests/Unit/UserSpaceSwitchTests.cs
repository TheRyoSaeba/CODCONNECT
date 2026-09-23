using CODConnect.Core.Diagnostics;

namespace CODConnect.UnitTests;

public class UserSpaceSwitchTests
{
    private static readonly MacAddress ConsoleMac = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x01);
    private static readonly MacAddress RemoteMac = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x02);
    private static readonly MacAddress StrangerMac = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x03);

    private static CapturedFrame Frame(byte[] bytes) => new(bytes, DateTimeOffset.UtcNow);

    private static byte[] ArpBroadcast(MacAddress source)
        => ArpPacket.BuildEthernetFrame(source, new IPv4Address(10, 42, 0, 1), new IPv4Address(10, 42, 0, 2), isReply: false, replyDestinationMac: MacAddress.None);

    private static byte[] Unicast(MacAddress destination, MacAddress source)
        => EthernetFrame.Build(destination, source, EthernetFrame.EtherTypeIpv4, new byte[8]);

    private sealed class EgressRecorder
    {
        public List<(int Port, CapturedFrame Frame)> Forwarded { get; } = new();

        public IReadOnlyList<int> PortsOfLast => Forwarded.Count == 0 ? [] : [Forwarded[^1].Port];

        public void Attach(UserSpaceSwitch sw)
            => sw.FrameForwarded += (port, frame) => Forwarded.Add((port, frame));

        public void Clear() => Forwarded.Clear();
    }

    private static (UserSpaceSwitch Sw, EgressRecorder Recorder, int ConsolePort, int PortB, int PortC) BuildSwitch(
        SwitchOptions? options = null,
        NetworkCounters? counters = null,
        Func<DateTimeOffset>? clock = null)
    {
        var sw = new UserSpaceSwitch(options, counters, clock);
        var recorder = new EgressRecorder();
        recorder.Attach(sw);
        var consolePort = sw.AddPort("console", new RecordingPort("console", ConsoleMac));
        var portB = sw.AddPort("b", new RecordingPort("b", RemoteMac));
        var portC = sw.AddPort("c", new RecordingPort("c", StrangerMac));
        sw.MarkConsolePort(consolePort);
        return (sw, recorder, consolePort, portB, portC);
    }

    [Fact]
    public void LocalMac_OnTunnelPort_IsDropped_NotLearned()
    {
        var counters = new NetworkCounters();
        var (sw, recorder, consolePort, portB, _) = BuildSwitch(
            new SwitchOptions { LocalMacs = new HashSet<MacAddress> { StrangerMac } }, counters);

        sw.HandleFrame(portB, Frame(ArpBroadcast(StrangerMac)));

        Assert.Empty(recorder.Forwarded);
        Assert.DoesNotContain(StrangerMac, sw.MacTableSnapshot().Keys);
        Assert.Equal(1, counters.FramesDropped);

        sw.HandleFrame(portB, Frame(ArpBroadcast(RemoteMac)));
        Assert.Contains(consolePort, recorder.Forwarded.Select(f => f.Port));
    }

    [Fact]
    public void LocalMac_OnConsolePort_IsNeverScopedInAsConsole()
    {
        var (sw, recorder, consolePort, portB, _) = BuildSwitch(
            new SwitchOptions { LocalMacs = new HashSet<MacAddress> { StrangerMac } });

        sw.HandleFrame(consolePort, Frame(ArpBroadcast(StrangerMac)));
        Assert.Empty(recorder.Forwarded);

        sw.HandleFrame(consolePort, Frame(ArpBroadcast(ConsoleMac)));
        Assert.Contains(portB, recorder.Forwarded.Select(f => f.Port));
    }

    [Fact]
    public void HyperVOui_OnConsolePort_IsNeverScopedInAsConsole()
    {
        var hyperV = new MacAddress(0x00, 0x15, 0x5D, 0x12, 0x34, 0x56);
        var (sw, recorder, consolePort, portB, _) = BuildSwitch();

        sw.HandleFrame(consolePort, Frame(ArpBroadcast(hyperV)));
        Assert.Empty(recorder.Forwarded);

        sw.HandleFrame(consolePort, Frame(ArpBroadcast(ConsoleMac)));
        Assert.Contains(portB, recorder.Forwarded.Select(f => f.Port));
    }

    [Fact]
    public void EchoFilter_MatchesPaddedEcho_Once()
    {
        var filter = new CODConnect.PacketEngine.EchoFilter();
        var injected = ArpBroadcast(ConsoleMac);
        var padded = new byte[60];
        injected.CopyTo(padded, 0);

        filter.Remember(injected);
        Assert.True(filter.IsEcho(padded));
        Assert.False(filter.IsEcho(padded));
        Assert.False(filter.IsEcho(ArpBroadcast(RemoteMac)));
    }

    [Fact]
    public void BroadcastFromConsolePort_FloodsToAllOtherPorts()
    {
        var (sw, recorder, consolePort, portB, portC) = BuildSwitch();
        sw.HandleFrame(consolePort, Frame(ArpBroadcast(ConsoleMac)));

        var egress = recorder.Forwarded.Select(f => f.Port).Order().ToArray();
        Assert.Equal(new[] { portB, portC }, egress);
    }

    [Fact]
    public void UnknownUnicast_FloodsToAllOtherPorts()
    {
        var (sw, recorder, consolePort, portB, portC) = BuildSwitch();
        sw.HandleFrame(consolePort, Frame(Unicast(RemoteMac, ConsoleMac)));

        var egress = recorder.Forwarded.Select(f => f.Port).Order().ToArray();
        Assert.Equal(new[] { portB, portC }, egress);
    }

    [Fact]
    public void LearnedUnicast_GoesOnlyToLearnedPort_AndReversePathLearns()
    {
        var (sw, recorder, consolePort, portB, portC) = BuildSwitch();

        sw.HandleFrame(portB, Frame(ArpBroadcast(RemoteMac)));
        recorder.Clear();

        sw.HandleFrame(consolePort, Frame(Unicast(RemoteMac, ConsoleMac)));
        Assert.Equal([portB], recorder.Forwarded.Select(f => f.Port).ToArray());
        recorder.Clear();

        sw.HandleFrame(portB, Frame(Unicast(ConsoleMac, RemoteMac)));
        Assert.Equal([consolePort], recorder.Forwarded.Select(f => f.Port).ToArray());
    }

    [Fact]
    public void UnicastToIngressPort_IsNotEchoedBack()
    {
        var (sw, recorder, consolePort, portB, _) = BuildSwitch();

        sw.HandleFrame(portB, Frame(ArpBroadcast(RemoteMac)));
        recorder.Clear();

        sw.HandleFrame(portB, Frame(Unicast(RemoteMac, RemoteMac)));
        Assert.Empty(recorder.Forwarded);
    }

    [Fact]
    public void StrictScoping_DropsForeignSourceOnConsolePort()
    {
        var counters = new NetworkCounters();
        var (sw, recorder, consolePort, _, _) = BuildSwitch(counters: counters);

        sw.HandleFrame(consolePort, Frame(ArpBroadcast(ConsoleMac)));
        Assert.NotEmpty(recorder.Forwarded);
        recorder.Clear();

        sw.HandleFrame(consolePort, Frame(ArpBroadcast(StrangerMac)));
        Assert.Empty(recorder.Forwarded);
        Assert.True(counters.FramesDropped >= 1);
    }

    [Fact]
    public void RegisterConsoleMac_ExplicitRegistrationIsHonored()
    {
        var (sw, recorder, consolePort, portB, _) = BuildSwitch();
        sw.RegisterConsoleMac(consolePort, ConsoleMac);

        sw.HandleFrame(consolePort, Frame(ArpBroadcast(StrangerMac)));
        Assert.Empty(recorder.Forwarded);

        sw.HandleFrame(consolePort, Frame(ArpBroadcast(ConsoleMac)));
        Assert.Equal(2, recorder.Forwarded.Count);
    }

    [Fact]
    public void RemoveStaleEntries_EvictsOnlyAgedEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var (sw, recorder, consolePort, portB, portC) = BuildSwitch(clock: () => now);

        sw.HandleFrame(portB, Frame(ArpBroadcast(RemoteMac)));
        Assert.Contains(RemoteMac, sw.MacTableSnapshot().Keys);

        now += TimeSpan.FromSeconds(400);
        sw.RemoveStaleEntries();
        Assert.DoesNotContain(RemoteMac, sw.MacTableSnapshot().Keys);

        recorder.Clear();
        sw.HandleFrame(consolePort, Frame(Unicast(RemoteMac, ConsoleMac)));
        Assert.Equal(new[] { portB, portC }, recorder.Forwarded.Select(f => f.Port).Order().ToArray());
    }

    [Fact]
    public void Counters_TrackBroadcastsAndMalformedFrames()
    {
        var counters = new NetworkCounters();
        var (sw, recorder, consolePort, portB, _) = BuildSwitch(counters: counters);

        sw.HandleFrame(consolePort, Frame(ArpBroadcast(ConsoleMac)));
        Assert.Equal(1, counters.BroadcastFramesIn);
        Assert.Equal(2, recorder.Forwarded.Count);

        sw.HandleFrame(consolePort, Frame(new byte[] { 1, 2, 3, 4 }));
        Assert.Equal(1, counters.MalformedFrames);
        Assert.Equal(2, recorder.Forwarded.Count);
    }

    [Fact]
    public void MaxLearnedMacs_EvictsOldestEntry()
    {
        var options = new SwitchOptions { MaxLearnedMacs = 2 };
        var (sw, recorder, consolePort, portB, portC) = BuildSwitch(options: options);

        sw.HandleFrame(portB, Frame(ArpBroadcast(RemoteMac)));
        sw.HandleFrame(portC, Frame(ArpBroadcast(StrangerMac)));
        Assert.Equal(2, sw.MacTableSnapshot().Count);

        sw.HandleFrame(consolePort, Frame(ArpBroadcast(ConsoleMac)));
        Assert.Equal(2, sw.MacTableSnapshot().Count);
        Assert.DoesNotContain(RemoteMac, sw.MacTableSnapshot().Keys);
        Assert.Contains(ConsoleMac, sw.MacTableSnapshot().Keys);
    }

    [Fact]
    public void GatewayPort_TalksOnlyWithConsolePorts()
    {
        var (sw, recorder, consolePort, tunnelPort, gatewayPort) = BuildSwitch();
        sw.MarkGatewayPort(gatewayPort);

        sw.HandleFrame(consolePort, Frame(ArpBroadcast(ConsoleMac)));
        Assert.Equal([tunnelPort, gatewayPort], recorder.Forwarded.Select(f => f.Port).Order());

        recorder.Clear();
        sw.HandleFrame(tunnelPort, Frame(ArpBroadcast(RemoteMac)));
        Assert.Equal([consolePort], recorder.Forwarded.Select(f => f.Port));

        recorder.Clear();
        sw.HandleFrame(gatewayPort, Frame(ArpBroadcast(StrangerMac)));
        Assert.Equal([consolePort], recorder.Forwarded.Select(f => f.Port));

        recorder.Clear();
        sw.HandleFrame(tunnelPort, Frame(Unicast(StrangerMac, RemoteMac)));
        Assert.Empty(recorder.Forwarded);

        recorder.Clear();
        sw.HandleFrame(gatewayPort, Frame(Unicast(new MacAddress(0x02, 0, 0, 0, 0, 0x7F), StrangerMac)));
        Assert.Equal([consolePort], recorder.Forwarded.Select(f => f.Port));
    }

    [Fact]
    public void RemovePort_ForgetsMacsLearnedOnIt()
    {
        var (sw, recorder, consolePort, tunnelPort, _) = BuildSwitch();
        sw.HandleFrame(tunnelPort, Frame(ArpBroadcast(RemoteMac)));
        Assert.Equal(tunnelPort, sw.MacTableSnapshot()[RemoteMac]);

        sw.RemovePort(tunnelPort);
        var replacement = sw.AddPort("tunnel-2", new RecordingPort("tunnel-2", RemoteMac));
        Assert.DoesNotContain(RemoteMac, sw.MacTableSnapshot().Keys);

        recorder.Clear();
        sw.HandleFrame(consolePort, Frame(Unicast(RemoteMac, ConsoleMac)));
        Assert.Contains(replacement, recorder.Forwarded.Select(f => f.Port));
    }

    [Fact]
    public void FrameToThisPc_IsNeverForwarded()
    {
        var pcMac = new MacAddress(0x02, 0x00, 0x00, 0x00, 0x00, 0x99);
        var (sw, recorder, consolePort, _, _) = BuildSwitch(new SwitchOptions { LocalMacs = new HashSet<MacAddress> { pcMac } });

        sw.HandleFrame(consolePort, Frame(Unicast(pcMac, ConsoleMac)));

        Assert.Empty(recorder.Forwarded);
    }
}
