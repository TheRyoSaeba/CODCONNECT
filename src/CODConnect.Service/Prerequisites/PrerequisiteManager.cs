using System.Diagnostics;
using System.ServiceProcess;
using CODConnect.Core.State;
using CODConnect.PacketEngine;

namespace CODConnect.Service.Prerequisites;

public sealed record PrerequisiteReport(string Name, bool Required, bool Installed, string Detail);

public sealed class PrerequisiteOptions
{
    public string PayloadDirectory { get; init; } =
        Path.Combine(AppContext.BaseDirectory, "prereqs");

    public TimeSpan InstallerTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed class PrerequisiteManager
{
    private readonly PrerequisiteOptions _options;
    private readonly ChangeLedger _ledger;
    private readonly Func<bool> _npcapInstalled;
    private readonly Func<bool> _softEtherServerInstalled;
    private readonly Func<bool> _softEtherClientInstalled;
    private readonly Func<string, string, TimeSpan, bool> _runInstaller;

    public PrerequisiteManager(
        PrerequisiteOptions? options = null,
        ChangeLedger? ledger = null,
        Func<bool>? npcapInstalled = null,
        Func<bool>? softEtherClientInstalled = null,
        Func<string, string, TimeSpan, bool>? runInstaller = null,
        Func<bool>? softEtherServerInstalled = null)
    {
        _options = options ?? new PrerequisiteOptions();
        _ledger = ledger ?? new ChangeLedger();
        _npcapInstalled = npcapInstalled ?? (() => PcapAdapters.IsAvailable);
        _softEtherServerInstalled = softEtherServerInstalled ?? (() => IsServicePresent(SoftEtherServerService));
        _softEtherClientInstalled = softEtherClientInstalled ?? (() => IsServicePresent(SoftEtherClientService));
        _runInstaller = runInstaller ?? RunInstallerAndWait;
    }

    public IReadOnlyList<PrerequisiteReport> Inspect()
    {
        return
        [
            InspectOne("Npcap", _npcapInstalled, FindPayload("npcap")),
            InspectOne("SoftEther VPN Server", _softEtherServerInstalled, FindPayload("softether-server")),
            InspectOne("SoftEther VPN Client", _softEtherClientInstalled, payload: null),
        ];
    }

    public IReadOnlyList<PrerequisiteReport> EnsureInstalled()
    {
        var results = new List<PrerequisiteReport>();
        foreach (var (name, detector, payload) in new[]
                 {
                     ("Npcap", _npcapInstalled, FindPayload("npcap")),
                     ("SoftEther VPN Server", _softEtherServerInstalled, FindPayload("softether-server")),
                     ("SoftEther VPN Client", _softEtherClientInstalled, null),
                 })
        {
            var report = EnsureOne(name, detector, payload);
            results.Add(report);
        }

        return results;
    }

    private PrerequisiteReport InspectOne(string name, Func<bool> installed, string? payload)
    {
        var isInstalled = SafeDetect(installed);
        return new PrerequisiteReport(
            name,
            Required: true,
            Installed: isInstalled,
            Detail: isInstalled
                ? "present"
                : payload is null
                    ? (name == "SoftEther VPN Client"
                        ? "missing: run the SoftEther VPN Client installer from the CODCONNECT prereqs folder"
                        : "missing, no bundled payload")
                    : "missing, bundled payload available");
    }

    private PrerequisiteReport EnsureOne(string name, Func<bool> installed, string? payload)
    {
        if (SafeDetect(installed))
        {
            return new PrerequisiteReport(name, true, true, "present");
        }

        if (payload is null)
        {
            return new PrerequisiteReport(name, true, false, name == "SoftEther VPN Client"
                ? "missing: run the SoftEther VPN Client installer from the CODCONNECT prereqs folder"
                : "missing, no bundled payload");
        }

        _ledger.Append("prerequisite-install-start", $"{name}: launching {Path.GetFileName(payload)}");
        var success = false;
        try
        {
            success = _runInstaller(name, payload, _options.InstallerTimeout);
        }
        catch (Exception ex)
        {
            _ledger.Append("prerequisite-install-error", $"{name}: {ex.Message}");
            return new PrerequisiteReport(name, true, false, $"installer failed: {ex.Message}");
        }

        var stillMissing = !SafeDetect(installed);
        _ledger.Append(
            success && !stillMissing ? "prerequisite-install-complete" : "prerequisite-install-incomplete",
            $"{name}: installer exit {(success ? "ok" : "failed")}, detected={(stillMissing ? "no" : "yes")}");

        return new PrerequisiteReport(
            name,
            true,
            !stillMissing,
            success
                ? (stillMissing ? "installer ran but component not yet detected (reboot may be required)" : "installed")
                : name == "Npcap"
                    ? "the bundled Npcap installer did not install silently (the free edition requires its GUI; run prereqs/npcap-installer.exe from the install folder once, or bundle Npcap OEM - plan 29.5)"
                    : "installer returned failure");
    }

    private string? FindPayload(string prefix)
    {
        try
        {
            if (!Directory.Exists(_options.PayloadDirectory))
            {
                return null;
            }

            var directory = Path.Combine(_options.PayloadDirectory, prefix);
            var runtime = prefix switch
            {
                "softether-server" => "vpnserver_x64.exe",
                _ => null,
            };
            if (runtime is not null)
            {
                return File.Exists(Path.Combine(directory, runtime)) ? directory : null;
            }

            return Directory.GetFiles(_options.PayloadDirectory, $"{prefix}*.exe")
                .OrderByDescending(f => f)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private bool SafeDetect(Func<bool> detector)
    {
        try
        {
            return detector();
        }
        catch
        {
            return false;
        }
    }

    public const string SoftEtherServerService = "SEVPNSERVER";
    public const string SoftEtherClientService = "SEVPNCLIENT";

    private static bool IsServicePresent(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            _ = controller.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool RunInstallerAndWait(string name, string payload, TimeSpan timeout)
    {
        if (Directory.Exists(payload))
        {
            return InstallSoftEtherRuntime(name, payload, timeout);
        }

        var exit = RunAndWait(payload, "/S", Path.GetDirectoryName(payload)!, TimeSpan.FromMinutes(2));
        return exit == 0;
    }

    private static bool InstallSoftEtherRuntime(string name, string payloadDirectory, TimeSpan timeout)
    {
        var (folder, runtime, service) = name switch
        {
            "SoftEther VPN Server" => ("SoftEther VPN Server", "vpnserver_x64.exe", SoftEtherServerService),
            _ => ("SoftEther VPN Client", "vpnclient_x64.exe", SoftEtherClientService),
        };
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), folder);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(payloadDirectory))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        var exe = Path.Combine(target, runtime);
        if (RunAndWait(exe, "/install", target, timeout) != 0)
        {
            return false;
        }

        try
        {
            using var controller = new ServiceController(service);
            if (controller.Status != ServiceControllerStatus.Running)
            {
                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
        }

        return true;
    }

    private static int RunAndWait(string fileName, string arguments, string workingDirectory, TimeSpan timeout)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null)
        {
            return -1;
        }

        if (!process.WaitForExit((int)Math.Ceiling(timeout.TotalMilliseconds)))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return -1;
        }

        return process.ExitCode;
    }
}
