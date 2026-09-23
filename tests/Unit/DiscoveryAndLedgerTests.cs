using CODConnect.Core.State;

namespace CODConnect.UnitTests;

public class OuiAndLedgerTests
{
    [Fact]
    public void PlayStationOui_AllPrefixes_AreSixHexDigits()
    {
        Assert.NotEmpty(PlayStationOui.Prefixes);
        Assert.All(PlayStationOui.Prefixes, prefix =>
        {
            Assert.Equal(6, prefix.Length);
            Assert.True(prefix.All(Uri.IsHexDigit), $"{prefix} is not hex");
        });
    }

    [Fact]
    public void ConsoleDetector_KnownPlayStationOui_IsDetected()
    {
        var first = PlayStationOui.Prefixes[0];
        var bytes = Convert.FromHexString(first + "A1B2C3");
        var mac = new MacAddress(bytes);
        Assert.Equal("PlayStation", ConsoleDetector.DetectConsoleType(mac));
    }

    [Fact]
    public void ConsoleDetector_HostnameAndSsdpSignals_WinAndXboxDetected()
    {
        Assert.Equal("PS5", ConsoleDetector.DetectConsoleType(MacAddress.None, dhcpHostname: "PS5-abc123"));
        Assert.Equal("Xbox", ConsoleDetector.DetectConsoleType(MacAddress.None, dhcpHostname: "XBOXONE"));
        Assert.Null(ConsoleDetector.DetectConsoleType(MacAddress.None));
    }

    [Fact]
    public void ConsoleDetector_UnknownMac_IsNotClaimedAsConsole()
    {
        Assert.Null(ConsoleDetector.DetectConsoleType(new MacAddress(0x00, 0x15, 0x5D, 0x00, 0x00, 0x01)));
    }

    [Fact]
    public void ChangeLedger_AppendReadRoundTrip_ToleratesTornLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codconnect-tests", Guid.NewGuid().ToString("N"));
        var ledger = new ChangeLedger(dir);

        ledger.Append("prerequisite-install-complete", "Npcap installed");
        ledger.Append("firewall-rule-added", "allow udp 47777", payload: "netsh ...");

        var records = ledger.ReadAll();
        Assert.Equal(2, records.Count);
        Assert.Equal("Npcap installed", records[0].Detail);
        Assert.Equal("netsh ...", records[1].Payload);

        File.AppendAllText(ledger.FilePath, "{ torn line from a crash\n");
        Assert.Equal(2, ledger.ReadAll().Count);
    }
}
