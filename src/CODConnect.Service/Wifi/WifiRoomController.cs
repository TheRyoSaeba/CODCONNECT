using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CODConnect.Core.State;
using CODConnect.Wifi;
using Microsoft.Win32;

namespace CODConnect.Service.Wifi;

public sealed record WifiUndoRecord(HotspotSettings Original, string RoomSsid, string GuardAddress, int? ForwardingInterfaceIndex = null, string? PcAddress = null, int? PcAddressInterfaceIndex = null);

public sealed class WifiJournal(string? path = null)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CODCONNECT.wifi");
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CODCONNECT", "wifi.bin");

    public void Write(WifiUndoRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        File.WriteAllBytes(_path, OperatingSystem.IsWindows() ? ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine) : bytes);
    }

    public WifiUndoRecord? Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(_path);
            if (OperatingSystem.IsWindows())
            {
                bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.LocalMachine);
            }

            return JsonSerializer.Deserialize<WifiUndoRecord>(bytes);
        }
        catch
        {
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(_path);
        }
        catch
        {
        }
    }
}

public interface IDhcpGuard
{
    void Apply(string hotspotAddress);

    void Remove();

    bool IsApplied { get; }
}

public sealed class NetshDhcpGuard : IDhcpGuard
{
    public const string RuleName = "CODConnect Wi-Fi DHCP guard";

    private readonly Func<string, (int ExitCode, string Output)> _netsh;

    public NetshDhcpGuard(Func<string, (int ExitCode, string Output)>? netsh = null)
    {
        _netsh = netsh ?? RunNetsh;
    }

    public void Apply(string hotspotAddress)
    {
        if (!System.Net.IPAddress.TryParse(hotspotAddress, out _))
        {
            throw new ArgumentException($"Not an IP address: {hotspotAddress}", nameof(hotspotAddress));
        }

        Remove();
        var (exit, output) = _netsh(
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=out action=block protocol=UDP localip={hotspotAddress} localport=67 profile=any");
        if (exit != 0 || !IsApplied)
        {
            throw new InvalidOperationException($"Could not keep Windows' DHCP off the console's Wi-Fi network: {output.Trim()}");
        }
    }

    public void Remove() => _netsh($"advfirewall firewall delete rule name=\"{RuleName}\"");

    public bool IsApplied => _netsh($"advfirewall firewall show rule name=\"{RuleName}\"").ExitCode == 0;

    internal static (int, string) RunNetsh(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("netsh", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            try { process.Kill(); } catch { }
            return (-1, "netsh timed out");
        }

        return (process.ExitCode, output.Result + error.Result);
    }
}

public interface IForwardingGuard
{
    int? Disable(string adapterId);

    void Restore(int interfaceIndex);

    int? IndexOf(string adapterId);

    void AddAddress(int interfaceIndex, string address, string mask);

    void RemoveAddress(int interfaceIndex, string address);
}

public sealed class NetshForwardingGuard : IForwardingGuard
{
    private readonly Func<string, (int ExitCode, string Output)> _netsh;

    public NetshForwardingGuard(Func<string, (int ExitCode, string Output)>? netsh = null)
    {
        _netsh = netsh ?? NetshDhcpGuard.RunNetsh;
    }

    public int? Disable(string adapterId)
    {
        var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => string.Equals(n.Id, adapterId, StringComparison.OrdinalIgnoreCase));
        var ipv4 = nic?.GetIPProperties().GetIPv4Properties();
        if (ipv4 is null || !ipv4.IsForwardingEnabled)
        {
            return null;
        }

        var (exit, output) = _netsh($"interface ipv4 set interface {ipv4.Index} forwarding=disabled store=active");
        if (exit != 0)
        {
            throw new InvalidOperationException($"Could not stop Windows routing the console's Wi-Fi traffic: {output.Trim()}");
        }

        return ipv4.Index;
    }

    public int? IndexOf(string adapterId)
        => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => string.Equals(n.Id, adapterId, StringComparison.OrdinalIgnoreCase))
            ?.GetIPProperties().GetIPv4Properties()?.Index;

    public void AddAddress(int interfaceIndex, string address, string mask)
    {
        RemoveAddress(interfaceIndex, address);
        var (exit, output) = _netsh($"interface ipv4 add address {interfaceIndex} {address} {mask} store=active");
        if (exit != 0)
        {
            throw new InvalidOperationException($"Could not give this PC an address on the console's network: {output.Trim()}");
        }
    }

    public void RemoveAddress(int interfaceIndex, string address)
    {
        if (Exists(interfaceIndex))
        {
            _netsh($"interface ipv4 delete address {interfaceIndex} {address} store=active");
        }
    }

    private static bool Exists(int interfaceIndex)
        => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Any(n => n.Supports(System.Net.NetworkInformation.NetworkInterfaceComponent.IPv4)
                      && n.GetIPProperties().GetIPv4Properties()?.Index == interfaceIndex);

    public void Restore(int interfaceIndex)
    {
        if (!Exists(interfaceIndex))
        {
            return;
        }

        var (exit, output) = _netsh($"interface ipv4 set interface {interfaceIndex} forwarding=enabled store=active");
        if (exit != 0)
        {
            throw new InvalidOperationException($"Could not restore routing on interface {interfaceIndex}: {output.Trim()}");
        }
    }
}

public sealed class WifiRoomController(IWifiHotspotManager hotspot, IDhcpGuard guard, WifiJournal journal, ChangeLedger? ledger = null, Action<string>? log = null, IForwardingGuard? forwarding = null)
{
    public const string SsidPrefix = "CODCONNECT-";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string HotspotScopeAddress()
    {
        try
        {
            if (OperatingSystem.IsWindows()
                && Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters", "ScopeAddress", null) is string configured
                && System.Net.IPAddress.TryParse(configured, out _))
            {
                return configured;
            }
        }
        catch
        {
        }

        return "192.168.137.1";
    }

    public Task<WifiCapabilityReport> CheckCapabilityAsync(CancellationToken cancellationToken = default)
        => hotspot.CheckCapabilityAsync(cancellationToken);

    public async Task<WifiHotspot> StartAsync(CancellationToken cancellationToken = default, (string Address, string Mask)? pcAddress = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await UndoLockedAsync(CancellationToken.None).ConfigureAwait(false);
            if (journal.Read() is not null)
            {
                throw new InvalidOperationException("The last Wi-Fi room could not be fully cleaned up yet. Try again in a moment.");
            }

            var status = await hotspot.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.IsOn)
            {
                throw new InvalidOperationException(
                    "Windows Mobile Hotspot is already on. CODCONNECT needs it for the console - turn it off in Settings, then try again.");
            }

            var room = new HotspotSettings(
                SsidPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(2)),
                RandomPassphrase(),
                "2.4 GHz");
            var address = HotspotScopeAddress();

            var record = new WifiUndoRecord(status.Settings, room.Ssid, address);
            journal.Write(record);
            ledger?.Append("wifi-room-start", $"hotspot {room.Ssid}; guard {address}");

            try
            {
                var started = await hotspot.StartHotspotAsync(room, cancellationToken).ConfigureAwait(false);
                guard.Apply(address);
                if (forwarding?.Disable(started.AdapterId) is { } index)
                {
                    record = record with { ForwardingInterfaceIndex = index };
                    journal.Write(record);
                }

                if (pcAddress is { } pc && forwarding?.IndexOf(started.AdapterId) is { } adapterIndex)
                {
                    record = record with { PcAddress = pc.Address, PcAddressInterfaceIndex = adapterIndex };
                    journal.Write(record);
                    forwarding.AddAddress(adapterIndex, pc.Address, pc.Mask);
                }

                log?.Invoke($"console Wi-Fi network {room.Ssid} up on {started.AdapterName} ({started.Settings.Band}); Windows DHCP blocked on {address}; Windows routing off on it");
                return started;
            }
            catch
            {
                await UndoLockedAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await UndoLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UndoLockedAsync(CancellationToken cancellationToken)
    {
        var record = journal.Read();
        if (record is null)
        {
            Step("remove DHCP guard", guard.Remove);
            return;
        }

        var ok = true;
        ok &= Step("remove DHCP guard", guard.Remove);
        if (record is { PcAddress: { } pcAddress, PcAddressInterfaceIndex: { } pcIndex } && forwarding is not null)
        {
            ok &= Step("remove this PC's console-network address", () => forwarding.RemoveAddress(pcIndex, pcAddress));
        }

        if (record.ForwardingInterfaceIndex is { } index && forwarding is not null)
        {
            ok &= Step("restore routing on the hotspot adapter", () => forwarding.Restore(index));
        }

        HotspotStatus? status = null;
        ok &= await StepAsync("read hotspot state", async () => status = await hotspot.GetStatusAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        if (status is not null && status.Settings.Ssid == record.RoomSsid)
        {
            if (status.IsOn)
            {
                ok &= await StepAsync("stop hotspot", () => hotspot.StopHotspotAsync(cancellationToken)).ConfigureAwait(false);
            }

            ok &= await StepAsync("restore the user's hotspot setting", () => hotspot.ApplySettingsAsync(record.Original, cancellationToken)).ConfigureAwait(false);
        }

        if (ok)
        {
            journal.Clear();
            ledger?.Append("wifi-room-undone", $"hotspot {record.RoomSsid} removed; user's setting '{record.Original.Ssid}' restored; guard removed");
            log?.Invoke($"console Wi-Fi network {record.RoomSsid} removed and the user's hotspot setting restored");
        }
        else
        {
            log?.Invoke($"console Wi-Fi cleanup for {record.RoomSsid} incomplete; it will be retried at the next start");
        }
    }

    private bool Step(string name, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Wi-Fi cleanup: {name} failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> StepAsync(string name, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Wi-Fi cleanup: {name} failed: {ex.Message}");
            return false;
        }
    }

    private static string RandomPassphrase()
    {
        return RandomNumberGenerator.GetString("abcdefghjkmnpqrstuvwxyz23456789", 8);
    }
}
