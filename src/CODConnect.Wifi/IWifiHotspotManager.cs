namespace CODConnect.Wifi;

public sealed record WifiCapabilityReport(
    bool Supported,
    string Reason,
    string? AdapterDescription = null,
    int? Channel = null,
    string? Band = null,
    IReadOnlyList<string>? Warnings = null);

public sealed record HotspotSettings(string Ssid, string Passphrase, string Band);

public sealed record HotspotStatus(bool IsOn, HotspotSettings Settings);

public sealed record WifiHotspot(
    HotspotSettings Settings,
    string AdapterName,
    string AdapterId,
    string? WindowsAddress);

public interface IWifiHotspotManager
{
    Task<WifiCapabilityReport> CheckCapabilityAsync(CancellationToken cancellationToken = default);

    Task<HotspotStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<WifiHotspot> StartHotspotAsync(HotspotSettings settings, CancellationToken cancellationToken = default);

    Task StopHotspotAsync(CancellationToken cancellationToken = default);

    Task ApplySettingsAsync(HotspotSettings settings, CancellationToken cancellationToken = default);
}

public sealed class UnsupportedWifiHotspotManager : IWifiHotspotManager
{
    private const string Message = "Wi-Fi mode needs Windows 10 (2004) or later.";

    public Task<WifiCapabilityReport> CheckCapabilityAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new WifiCapabilityReport(false, Message));

    public Task<HotspotStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new HotspotStatus(false, new HotspotSettings(string.Empty, string.Empty, "Auto")));

    public Task<WifiHotspot> StartHotspotAsync(HotspotSettings settings, CancellationToken cancellationToken = default)
        => throw new PlatformNotSupportedException(Message);

    public Task StopHotspotAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ApplySettingsAsync(HotspotSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
