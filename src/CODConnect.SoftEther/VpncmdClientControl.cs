using System.Diagnostics;
using System.Text;

namespace CODConnect.SoftEther;

public sealed record VpncmdResult(int ExitCode, string Output);

public sealed record SoftEtherClientSessionStatus(bool Established, string? Underlay, string? Detail);

public interface ISoftEtherClientControl
{
    Task EnsureVirtualAdapterAsync(string nicName, CancellationToken cancellationToken = default);

    Task ConnectAsync(string accountName, string host, int port, string hubName, string userName, string password, string nicName, CancellationToken cancellationToken = default);

    Task<SoftEtherClientSessionStatus> GetStatusAsync(string accountName, CancellationToken cancellationToken = default);

    Task DisconnectAsync(string accountName, CancellationToken cancellationToken = default);

    Task<bool> DisableIpBindingsAsync(string nicName, CancellationToken cancellationToken = default);
}

public sealed class VpncmdClientControl : ISoftEtherClientControl
{
    public const int ErrorAdapterNotFound = 29;
    public const int ErrorAdapterExists = 30;
    public const int ErrorInvalidAdapterName = 32;
    public const int ErrorAccountExists = 34;
    public const int ErrorAccountNotFound = 36;

    private readonly string _vpncmdPath;
    private readonly Func<string[], CancellationToken, Task<VpncmdResult>> _run;

    public VpncmdClientControl(string? vpncmdPath = null, Func<string[], CancellationToken, Task<VpncmdResult>>? run = null)
    {
        _vpncmdPath = vpncmdPath ?? FindVpncmd() ?? throw new FileNotFoundException("SoftEther VPN Client (vpncmd) is not installed.");
        _run = run ?? RunProcessAsync;
    }

    public static string? FindVpncmd()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            foreach (var name in new[] { "vpncmd_x64.exe", "vpncmd.exe" })
            {
                var candidate = Path.Combine(root, "SoftEther VPN Client", name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static bool IsInstalled => FindVpncmd() is not null;

    public async Task EnsureVirtualAdapterAsync(string nicName, CancellationToken cancellationToken = default)
    {
        var result = await _run(["NicCreate", nicName], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode is 0 or ErrorAdapterExists)
        {
            if (result.ExitCode == 0)
            {
                await _run(["NicEnable", nicName], cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (result.ExitCode == ErrorInvalidAdapterName)
        {
            throw new InvalidOperationException($"SoftEther rejected virtual adapter name '{nicName}'; Windows only accepts VPN, VPN2, VPN3, ...");
        }

        throw Failure("NicCreate", result);
    }

    public async Task ConnectAsync(string accountName, string host, int port, string hubName, string userName, string password, string nicName, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync(accountName, cancellationToken).ConfigureAwait(false);

        Expect(await _run(["AccountCreate", accountName, $"/SERVER:{host}:{port}", $"/HUB:{hubName}", $"/USERNAME:{userName}", $"/NICNAME:{nicName}"], cancellationToken).ConfigureAwait(false), "AccountCreate");
        Expect(await _run(["AccountPasswordSet", accountName, $"/PASSWORD:{password}", "/TYPE:standard"], cancellationToken).ConfigureAwait(false), "AccountPasswordSet");
        Expect(await _run(["AccountDetailSet", accountName, "/MAXTCP:1", "/INTERVAL:1", "/TTL:0", "/HALF:no", "/BRIDGE:no", "/MONITOR:no", "/NOTRACK:yes", "/NOQOS:no"], cancellationToken).ConfigureAwait(false), "AccountDetailSet");
        Expect(await _run(["AccountConnect", accountName], cancellationToken).ConfigureAwait(false), "AccountConnect");
    }

    public async Task<SoftEtherClientSessionStatus> GetStatusAsync(string accountName, CancellationToken cancellationToken = default)
    {
        var result = await _run(["AccountStatusGet", accountName], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return new SoftEtherClientSessionStatus(false, null, ExtractError(result.Output));
        }

        var fields = ParseTable(result.Output);
        fields.TryGetValue("Session Status", out var status);
        fields.TryGetValue("Physical Underlay Protocol", out var underlay);
        var established = status?.Contains("Established", StringComparison.OrdinalIgnoreCase) == true;
        return new SoftEtherClientSessionStatus(established, underlay, status);
    }

    public async Task DisconnectAsync(string accountName, CancellationToken cancellationToken = default)
    {
        await _run(["AccountDisconnect", accountName], cancellationToken).ConfigureAwait(false);
        var delete = await _run(["AccountDelete", accountName], cancellationToken).ConfigureAwait(false);
        if (delete.ExitCode is not (0 or ErrorAccountNotFound))
        {
            throw Failure("AccountDelete", delete);
        }
    }

    public async Task<bool> DisableIpBindingsAsync(string nicName, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var script =
            $"$a = Get-NetAdapter -ErrorAction Stop | Where-Object {{ $_.Name -eq '{nicName} - VPN Client' -or $_.InterfaceDescription -eq 'VPN Client Adapter - {nicName}' }}; " +
            "if (-not $a) { exit 2 }; " +
            "$a | Disable-NetAdapterBinding -ComponentID ms_tcpip,ms_tcpip6,ms_msclient,ms_server,ms_lltdio,ms_rspndr -ErrorAction Stop; " +
            "Get-NetIPInterface -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue | Set-NetIPInterface -Dhcp Disabled -RouterDiscovery Disabled -InterfaceMetric 9999 -ErrorAction SilentlyContinue; " +
            "Get-NetIPAddress -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue; " +
            "Get-NetRoute -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue | Where-Object { $_.DestinationPrefix -in '0.0.0.0/0', '::/0' } | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue; " +
            "exit 0";
        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            _ = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public static Dictionary<string, string> ParseTable(string output)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var separator = line.IndexOf('|');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length > 0 && !key.StartsWith("Item", StringComparison.Ordinal) && !key.StartsWith('-') && !fields.ContainsKey(key))
            {
                fields[key] = value;
            }
        }

        return fields;
    }

    private static string ExtractError(string output)
    {
        var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var index = lines.FindIndex(l => l.StartsWith("Error occurred", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < lines.Count ? lines[index + 1] : lines.LastOrDefault() ?? "unknown vpncmd error";
    }

    private static void Expect(VpncmdResult result, string command)
    {
        if (result.ExitCode != 0)
        {
            throw Failure(command, result);
        }
    }

    private static InvalidOperationException Failure(string command, VpncmdResult result)
        => new($"vpncmd {command} failed (code {result.ExitCode}): {ExtractError(result.Output)}");

    private async Task<VpncmdResult> RunProcessAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = _vpncmdPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("localhost");
        info.ArgumentList.Add("/CLIENT");
        info.ArgumentList.Add("/CMD");
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start vpncmd.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return new VpncmdResult(process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }
}
