using CODConnect.NetworkSimulation;

namespace CODConnect.UnitTests;

public class SwitchEngineTests
{
    private static readonly MacAddress ConsoleMac = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x01);
    private static readonly MacAddress RemoteMac = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x02);

    private static byte[] ArpBroadcast(MacAddress source)
        => ArpPacket.BuildEthernetFrame(source, new IPv4Address(10, 42, 0, 1), new IPv4Address(10, 42, 0, 2), isReply: false, replyDestinationMac: MacAddress.None);

    private static async Task<T?> WaitForAsync<T>(Func<T?> probe, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
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
    public async Task Engine_ForwardsBroadcastBetweenLinks_EndToEnd()
    {
        var (fakeConsoleA, portA) = VirtualPairLink.CreatePair("consoleA", "portA", ConsoleMac, MacAddress.None);
        var (fakeConsoleB, portB) = VirtualPairLink.CreatePair("consoleB", "portB", RemoteMac, MacAddress.None);

        var sw = new UserSpaceSwitch(new SwitchOptions { StrictConsoleScoping = true });
        await using var engine = new SwitchEngine(sw);
        engine.AddPort(portA, isConsolePort: true);
        engine.AddPort(portB, isConsolePort: false);
        await engine.StartAsync();

        var arpRequest = ArpPacket.BuildEthernetFrame(
            ConsoleMac, new IPv4Address(10, 42, 0, 1), new IPv4Address(10, 42, 0, 2), isReply: false, replyDestinationMac: MacAddress.None);
        await fakeConsoleA.InjectFrameAsync(arpRequest);

        var received = await WaitForAsync(() => fakeConsoleB.CaptureFrame());
        Assert.NotNull(received);
        Assert.True(received.Value.Buffer.Span.SequenceEqual(arpRequest));

        await engine.StopAsync();
    }

    [Fact]
    public async Task Engine_LearnedUnicast_GoesOnlyToRightLink()
    {
        var (fakeConsoleA, portA) = VirtualPairLink.CreatePair("consoleA", "portA", ConsoleMac, MacAddress.None);
        var (fakeConsoleB, portB) = VirtualPairLink.CreatePair("consoleB", "portB", RemoteMac, MacAddress.None);

        var sw = new UserSpaceSwitch(new SwitchOptions { StrictConsoleScoping = true });
        await using var engine = new SwitchEngine(sw);
        engine.AddPort(portA, isConsolePort: true);
        engine.AddPort(portB, isConsolePort: false);
        await engine.StartAsync();

        var arpRequest = ArpPacket.BuildEthernetFrame(
            ConsoleMac, new IPv4Address(10, 42, 0, 1), new IPv4Address(10, 42, 0, 2), isReply: false, replyDestinationMac: MacAddress.None);
        await fakeConsoleA.InjectFrameAsync(arpRequest);
        Assert.NotNull(await WaitForAsync(() => fakeConsoleB.CaptureFrame()));

        var arpReply = ArpPacket.BuildEthernetFrame(
            RemoteMac, new IPv4Address(10, 42, 0, 2), new IPv4Address(10, 42, 0, 1), isReply: true, replyDestinationMac: ConsoleMac);
        await fakeConsoleB.InjectFrameAsync(arpReply);
        Assert.NotNull(await WaitForAsync(() => fakeConsoleA.CaptureFrame()));

        var unicast = EthernetFrame.Build(ConsoleMac, RemoteMac, EthernetFrame.EtherTypeIpv4, new byte[8]);
        await fakeConsoleB.InjectFrameAsync(unicast);
        var unicastReceived = await WaitForAsync(() => fakeConsoleA.CaptureFrame());
        Assert.NotNull(unicastReceived);
        Assert.True(unicastReceived.Value.Buffer.Span.SequenceEqual(unicast));

        Assert.Null(fakeConsoleB.CaptureFrame());
        await engine.StopAsync();
    }

    [Fact]
    public async Task Engine_StopAsync_StopsForwarding()
    {
        var (fakeConsoleA, portA) = VirtualPairLink.CreatePair("consoleA", "portA", ConsoleMac, MacAddress.None);
        var (fakeConsoleB, portB) = VirtualPairLink.CreatePair("consoleB", "portB", RemoteMac, MacAddress.None);

        var sw = new UserSpaceSwitch(new SwitchOptions { StrictConsoleScoping = false });
        await using var engine = new SwitchEngine(sw);
        engine.AddPort(portA, isConsolePort: true);
        engine.AddPort(portB, isConsolePort: false);
        await engine.StartAsync();
        await engine.StopAsync();

        await fakeConsoleA.InjectFrameAsync(ArpBroadcast(ConsoleMac));
        await Task.Delay(100);
        Assert.Null(fakeConsoleB.CaptureFrame());
    }
}
