using CODConnect.Discovery;
using CODConnect.PacketEngine.Dhcp;

namespace CODConnect.UnitTests;

public class ConsoleIdentificationTests
{
    private static readonly MacAddress Ps4WifiMac = new(0x28, 0x66, 0xE3, 0x51, 0x7B, 0x01);

    private const string Ps4DhcpRequest =
        "ffffffffffff2866e3517b01080045000148aa0100001011ffa400000000ffffffff0044004301349853010106004f7fde7700000000000000000000000000000000000000002866e3517b0100000000"
        + "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "00000000000000000000000000000000000000000000000000000000000000000000000000006382536335010332040a2a000236040a2a0001370401030f063d07012866e3517b013c0450533400ff00"
        + "00000000000000000000000000000000000000000000";

    private const string Ps4MdnsResponse =
        "01005e0000fb2866e3517b0108004500012761370000ff116e670a2a0002e00000fb14e914e90113a0e60000840000000006000000030132013002343202313007696e2d61646472046172706100000c80010000007800120a5053342d423935304245056c6f63616c000b53705a632d423935304245105f73706f746966792d636f6e6e656374045f746370c0390010800100001194000e0d43506174683d2f7370436f6e6e095f7365727669636573075f646e732d7364045f756470c039000c0001000011940002c04cc04c000c0001000011940002c040c02e000180010000007800040a2a0002c0400021800100000078000800000000a348c02ec00c002f8001000000780006c00c00020008c040002f8001000011940009c04000050000800040c02e002f8001000000780005c02e000140";

    private static ReadOnlySpan<byte> UdpPayload(byte[] frame, out IPv4Address source)
    {
        Assert.True(EthernetFrame.TryParse(frame, out var ethernet));
        Assert.True(IPv4Packet.TryParse(ethernet.Payload, out var ip));
        Assert.True(UdpPacket.TryParse(ip.Payload, out var udp));
        source = ip.Source;
        return udp.Payload;
    }

    [Fact]
    public void RealPs4DhcpRequest_CarriesVendorClassPs4()
    {
        Assert.True(DhcpPacket.TryParse(UdpPayload(Convert.FromHexString(Ps4DhcpRequest), out _), out var dhcp));
        Assert.Equal("PS4", dhcp.VendorClass);
        Assert.Equal(string.Empty, dhcp.Hostname);
        Assert.Equal("PS4", ConsoleDetector.DetectConsoleType(Ps4WifiMac, dhcp.Hostname, vendorClass: dhcp.VendorClass));
    }

    [Fact]
    public void RealPs4MdnsResponse_YieldsItsOwnName()
    {
        var payload = UdpPayload(Convert.FromHexString(Ps4MdnsResponse), out var source);
        Assert.True(MdnsPacket.TryGetOwnHostName(payload, source, out var name));
        Assert.Equal("PS4-B950BE", name);
        Assert.Equal("PS4", ConsoleDetector.DetectConsoleType(Ps4WifiMac, name));
    }

    [Fact]
    public void MdnsResponse_ForAnotherAddress_IsNotTheSendersName()
    {
        var payload = UdpPayload(Convert.FromHexString(Ps4MdnsResponse), out _);
        Assert.False(MdnsPacket.TryGetOwnHostName(payload, new IPv4Address(10, 42, 0, 9), out _));
    }

    [Fact]
    public void TruncatedMdns_NeverThrows_NeverMisnames()
    {
        var payload = UdpPayload(Convert.FromHexString(Ps4MdnsResponse), out var source).ToArray();
        for (var length = 0; length < payload.Length; length++)
        {
            if (MdnsPacket.TryGetOwnHostName(payload.AsSpan(0, length), source, out var name))
            {
                Assert.Equal("PS4-B950BE", name);
            }
        }

        Assert.False(MdnsPacket.TryGetOwnHostName(payload.AsSpan(0, 40), source, out _));
    }

    [Fact]
    public void PlayStationRadioMac_WithoutAnyStatement_IsUnknown()
        => Assert.Null(ConsoleDetector.DetectConsoleType(Ps4WifiMac));
}

public class LinkStateCacheTests
{
    [Fact]
    public void NoAnswerYet_IsNeverFresh()
        => Assert.False(NpcapConsoleInterface.IsFresh(Environment.TickCount64, null, TimeSpan.FromSeconds(1)));

    [Fact]
    public void Answer_IsFreshOnlyWithinItsTtl()
    {
        Assert.True(NpcapConsoleInterface.IsFresh(10_500, 10_000, TimeSpan.FromSeconds(1)));
        Assert.False(NpcapConsoleInterface.IsFresh(11_000, 10_000, TimeSpan.FromSeconds(1)));
    }
}
