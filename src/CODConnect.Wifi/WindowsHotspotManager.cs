using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace CODConnect.Wifi;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsHotspotManager : IWifiHotspotManager
{
    public TimeSpan AdapterWaitTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public Task<WifiCapabilityReport> CheckCapabilityAsync(CancellationToken cancellationToken = default)
    {
        var wlan = NativeWifi.Interfaces();
        var radio = wlan.FirstOrDefault(i => i.Connected) ?? wlan.FirstOrDefault();
        if (radio is null)
        {
            return Task.FromResult(new WifiCapabilityReport(false,
                "This PC has no Wi-Fi adapter. Use Ethernet mode, or add a USB Wi-Fi adapter."));
        }

        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile is null)
        {
            return Task.FromResult(new WifiCapabilityReport(false,
                "Windows only hosts a Wi-Fi network while this PC is online. Connect it to the Internet first.",
                radio.Description, radio.Channel, radio.Band));
        }

        var capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile);
        if (capability != TetheringCapability.Enabled)
        {
            return Task.FromResult(new WifiCapabilityReport(false, Explain(capability, radio.Description),
                radio.Description, radio.Channel, radio.Band));
        }

        var warnings = new List<string>();
        if (radio.OnDfsChannel)
        {
            warnings.Add($"This PC's Wi-Fi is on 5 GHz channel {radio.Channel}, a radar (DFS) channel. The console's network shares it, "
                + "and some consoles will not join there. A 2.4 GHz or non-DFS 5 GHz home network is more reliable.");
        }

        return Task.FromResult(new WifiCapabilityReport(true, "This PC can host the console's Wi-Fi network.",
            radio.Description, radio.Channel, radio.Band, warnings));
    }

    public Task<HotspotStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var manager = Manager();
        var configuration = manager.GetCurrentAccessPointConfiguration();
        return Task.FromResult(new HotspotStatus(
            manager.TetheringOperationalState == TetheringOperationalState.On,
            new HotspotSettings(configuration.Ssid, configuration.Passphrase, BandOf(configuration))));
    }

    public async Task<WifiHotspot> StartHotspotAsync(HotspotSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.Passphrase.Length is < 8 or > 63)
        {
            throw new ArgumentException("The Wi-Fi password must be 8-63 characters.", nameof(settings));
        }

        var report = await CheckCapabilityAsync(cancellationToken).ConfigureAwait(false);
        if (!report.Supported)
        {
            throw new InvalidOperationException(report.Reason);
        }

        var manager = Manager();
        if (manager.TetheringOperationalState == TetheringOperationalState.On)
        {
            throw new InvalidOperationException("Windows Mobile Hotspot is already on. Turn it off in Settings, then try again.");
        }

        await ApplyAsync(manager, settings, cancellationToken).ConfigureAwait(false);
        var result = await manager.StartTetheringAsync().AsTask(cancellationToken).ConfigureAwait(false);
        if (result.Status != TetheringOperationStatus.Success)
        {
            throw new InvalidOperationException(
                $"Windows could not start the console's Wi-Fi network ({result.Status}"
                + $"{(string.IsNullOrWhiteSpace(result.AdditionalErrorMessage) ? "" : ": " + result.AdditionalErrorMessage)}).");
        }

        var adapter = await WaitForHotspotAdapterAsync(cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("Windows reported the hotspot as started, but its network adapter never came up.");
        var address = adapter.GetIPProperties().UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
        return new WifiHotspot(settings with { Band = BandOf(manager.GetCurrentAccessPointConfiguration()) }, adapter.Name, adapter.Id, address);
    }

    public async Task StopHotspotAsync(CancellationToken cancellationToken = default)
    {
        var manager = Manager();
        if (manager.TetheringOperationalState != TetheringOperationalState.Off)
        {
            await manager.StopTetheringAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ApplySettingsAsync(HotspotSettings settings, CancellationToken cancellationToken = default)
        => ApplyAsync(Manager(), settings, cancellationToken);

    public static NetworkInterface? FindHotspotAdapter()
        => NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            n.Description.StartsWith("Microsoft Wi-Fi Direct Virtual Adapter", StringComparison.OrdinalIgnoreCase)
            && n.OperationalStatus == OperationalStatus.Up);

    private static NetworkOperatorTetheringManager Manager()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile()
                      ?? throw new InvalidOperationException("Windows only hosts a Wi-Fi network while this PC is online.");
        return NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
    }

    private static async Task ApplyAsync(NetworkOperatorTetheringManager manager, HotspotSettings settings, CancellationToken cancellationToken)
    {
        var configuration = manager.GetCurrentAccessPointConfiguration();
        configuration.Ssid = settings.Ssid;
        configuration.Passphrase = settings.Passphrase;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            SetBand(configuration, settings.Band);
        }

        await manager.ConfigureAccessPointAsync(configuration).AsTask(cancellationToken).ConfigureAwait(false);
    }

    private async Task<NetworkInterface?> WaitForHotspotAdapterAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + AdapterWaitTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (FindHotspotAdapter() is { } adapter)
            {
                return adapter;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static string BandOf(NetworkOperatorTetheringAccessPointConfiguration configuration)
        => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? BandName(configuration.Band) : "Auto";

    [SupportedOSPlatform("windows10.0.22000.0")]
    private static string BandName(TetheringWiFiBand band) => band switch
    {
        TetheringWiFiBand.TwoPointFourGigahertz => "2.4 GHz",
        TetheringWiFiBand.FiveGigahertz => "5 GHz",
        _ => "Auto",
    };

    [SupportedOSPlatform("windows10.0.22000.0")]
    private static void SetBand(NetworkOperatorTetheringAccessPointConfiguration configuration, string band)
    {
        var wanted = band switch
        {
            "2.4 GHz" => TetheringWiFiBand.TwoPointFourGigahertz,
            "5 GHz" => TetheringWiFiBand.FiveGigahertz,
            _ => TetheringWiFiBand.Auto,
        };
        configuration.Band = wanted == TetheringWiFiBand.Auto || configuration.IsBandSupported(wanted) ? wanted : TetheringWiFiBand.Auto;
    }

    private static string Explain(TetheringCapability capability, string adapter) => capability switch
    {
        TetheringCapability.DisabledByHardwareLimitation =>
            $"This PC's Wi-Fi ({adapter}) cannot host a network - its driver does not support it. Use Ethernet mode, or a USB Wi-Fi adapter that can host a hotspot.",
        TetheringCapability.DisabledByGroupPolicy =>
            "Hosting a Wi-Fi network is turned off by this PC's policy (common on work PCs). Use Ethernet mode.",
        TetheringCapability.DisabledBySystemCapability =>
            "Windows reports that this PC cannot host a Wi-Fi network. Use Ethernet mode.",
        TetheringCapability.DisabledBySku =>
            "This edition of Windows does not allow hosting a Wi-Fi network. Use Ethernet mode.",
        TetheringCapability.DisabledByOperator or TetheringCapability.DisabledByRequiredAppNotInstalled =>
            "This PC's Internet connection does not allow sharing (typical for some mobile-data connections). Use Ethernet mode.",
        _ => "Windows cannot host a Wi-Fi network on this PC right now. Use Ethernet mode.",
    };
}
