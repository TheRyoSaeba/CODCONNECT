using CODConnect.Core.State;
using CODConnect.Service.Prerequisites;

namespace CODConnect.IntegrationTests;

public class PrerequisiteTests
{
    private static (PrerequisiteManager Manager, ChangeLedger Ledger, List<string> Ran) CreateManager(
        bool npcapPresent, bool softEtherPresent, string? npcapPayload, string? softEtherPayload)
    {
        var dir = Path.Combine(Path.GetTempPath(), "codconnect-tests", Guid.NewGuid().ToString("N"));
        var ledger = new ChangeLedger(dir);
        var ran = new List<string>();

        var options = new PrerequisiteOptions { PayloadDirectory = dir };
        if (npcapPayload is not null || softEtherPayload is not null)
        {
            Directory.CreateDirectory(dir);
            if (npcapPayload is not null)
            {
                File.WriteAllText(Path.Combine(dir, "npcap-installer.exe"), "stub");
            }

            if (softEtherPayload is not null)
            {
                Directory.CreateDirectory(Path.Combine(dir, "softether-server"));
                File.WriteAllText(Path.Combine(dir, "softether-server", "vpnserver_x64.exe"), "stub");
                Directory.CreateDirectory(Path.Combine(dir, "softether-client"));
                File.WriteAllText(Path.Combine(dir, "softether-client", "vpnclient_x64.exe"), "stub");
            }
        }

        var npcap = npcapPresent;
        var softEther = softEtherPresent;
        var manager = new PrerequisiteManager(
            options,
            ledger,
            npcapInstalled: () => npcap,
            softEtherClientInstalled: () => softEther,
            softEtherServerInstalled: () => softEther,
            runInstaller: (name, payload, timeout) =>
            {
                ran.Add(name);
                if (name == "Npcap")
                {
                    npcap = true;
                }
                else
                {
                    softEther = true;
                }

                return true;
            });
        return (manager, ledger, ran);
    }

    [Fact]
    public void Inspect_WhenAllPresent_ReportsPresent()
    {
        var (manager, _, _) = CreateManager(npcapPresent: true, softEtherPresent: true, null, null);
        var reports = manager.Inspect();

        Assert.All(reports, r => Assert.True(r.Installed));
        Assert.Equal(3, reports.Count);
    }

    [Fact]
    public void EnsureInstalled_MissingWithPayload_RunsInstallerAndLedgers()
    {
        var (manager, ledger, ran) = CreateManager(npcapPresent: false, softEtherPresent: false, "stub", "stub");
        var reports = manager.EnsureInstalled();

        Assert.Equal(["Npcap", "SoftEther VPN Server"], ran);
        Assert.All(reports, r => Assert.True(r.Installed));
        var kinds = ledger.ReadAll().Select(r => r.Kind).ToList();
        Assert.Contains("prerequisite-install-start", kinds);
        Assert.Contains("prerequisite-install-complete", kinds);
    }

    [Fact]
    public void EnsureInstalled_MissingWithoutPayload_ReportsWithoutRunning()
    {
        var (manager, ledger, ran) = CreateManager(npcapPresent: false, softEtherPresent: true, null, null);
        var reports = manager.EnsureInstalled();

        Assert.Empty(ran);
        var npcap = reports.Single(r => r.Name == "Npcap");
        Assert.False(npcap.Installed);
        Assert.Contains("no bundled payload", npcap.Detail);
        Assert.Empty(ledger.ReadAll());
    }

    [Fact]
    public void EnsureInstalled_InstallerFails_ReportsIncomplete()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codconnect-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "npcap-installer.exe"), "stub");
        var ledger = new ChangeLedger(dir);
        var manager = new PrerequisiteManager(
            new PrerequisiteOptions { PayloadDirectory = dir },
            ledger,
            npcapInstalled: () => false,
            softEtherClientInstalled: () => true,
            runInstaller: (_, _, _) => false);

        var report = manager.EnsureInstalled().Single(r => r.Name == "Npcap");

        Assert.False(report.Installed);
        Assert.Contains("prerequisite-install-incomplete", ledger.ReadAll().Select(r => r.Kind));
    }
}
