using CODConnect.Service;

namespace CODConnect.IntegrationTests;

public class AdapterSelectionTests
{
    private static PcapAdapterInfo Adapter(string name, LinkState link = LinkState.Up, bool loopback = false)
        => new(
            PcapName: $@"\Device\NPF_{{{name}}}",
            FriendlyName: name,
            Description: string.Empty,
            Addresses: [],
            Mac: new MacAddress(0x02, 0x00, 0x00, 0x00, 0x00, 0x01),
            LinkState: link,
            IsLoopback: loopback);

    private static readonly PcapAdapterInfo Realtek = Adapter("Ethernet");
    private static readonly PcapAdapterInfo Wifi = Adapter("WiFi");
    private static readonly PcapAdapterInfo Wsl = Adapter("vEthernet (WSL)");
    private static readonly PcapAdapterInfo Vpn = Adapter("VPN9 - VPN Client");
    private static readonly PcapAdapterInfo Unplugged = Adapter("Ethernet 2", LinkState.Down);

    private static PcapAdapterInfo Select(IReadOnlyList<PcapAdapterInfo> adapters, string? selector, bool allowInternet = false)
        => ConsoleAdapterFactory.SelectAdapter(
            adapters,
            selector,
            allowInternet,
            isInternet: a => a.FriendlyName == "WiFi",
            isEthernet: a => a.FriendlyName.StartsWith("Ethernet", StringComparison.Ordinal));

    [Fact]
    public void Automatic_PrefersFreeEthernet_OverVirtualAndInternetAdapters()
    {
        var chosen = Select([Wsl, Wifi, Vpn, Realtek], selector: null);
        Assert.Equal("Ethernet", chosen.FriendlyName);
    }

    [Fact]
    public void Automatic_SkipsUnpluggedAndLoopback()
    {
        var chosen = Select([Adapter("loopback", loopback: true), Unplugged, Realtek], selector: null);
        Assert.Equal("Ethernet", chosen.FriendlyName);
    }

    [Fact]
    public void Automatic_WhenOnlyTheInternetAdapterRemains_Fails()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Select([Wifi, Wsl, Vpn], selector: null));
        Assert.Contains("No spare connected adapter", error.Message);
    }

    [Fact]
    public void ExactName_WinsOverSubstring()
    {
        var chosen = Select([Wsl, Realtek], "Ethernet");
        Assert.Equal("Ethernet", chosen.FriendlyName);
    }

    [Fact]
    public void Substring_StillMatchesWhenNoExactName()
    {
        var chosen = Select([Wsl, Realtek], "WSL");
        Assert.Equal("vEthernet (WSL)", chosen.FriendlyName);
    }

    [Fact]
    public void InternetAdapter_IsRefusedUnlessConfirmed()
    {
        var error = Assert.Throws<ConsoleAdapterFactory.InternetAdapterRefusedException>(() => Select([Realtek, Wifi], "WiFi"));
        Assert.Equal("WiFi", error.AdapterName);
        Assert.Contains("carrying this PC's Internet", error.Message);

        var chosen = Select([Realtek, Wifi], "WiFi", allowInternet: true);
        Assert.Equal("WiFi", chosen.FriendlyName);
    }

    [Fact]
    public void UnknownSelector_Fails()
        => Assert.Throws<ArgumentException>(() => Select([Realtek], "nosuch"));
}
