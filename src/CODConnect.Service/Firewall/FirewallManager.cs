using System.Diagnostics;
using CODConnect.Core.State;

namespace CODConnect.Service.Firewall;

public sealed record FirewallRule(string Name, string Protocol, string LocalPorts);

public sealed record FirewallReport(string Rule, bool Present, string Action);

public sealed class FirewallManager
{
    public static readonly FirewallRule[] DefaultRules =
    [
        new("CODConnect Tunnel (TCP)", "TCP", SessionDefaults.TunnelListenPort.ToString()),
        new("CODConnect SoftEther (TCP)", "TCP", "5555,443"),
        new("CODConnect SoftEther (UDP)", "UDP", "5555"),
    ];

    private readonly ChangeLedger _ledger;
    private readonly Func<string, bool> _ruleExists;
    private readonly Action<string, string> _runNetsh;

    public FirewallManager(
        ChangeLedger? ledger = null,
        Func<string, bool>? ruleExists = null,
        Action<string, string>? runNetsh = null)
    {
        _ledger = ledger ?? new ChangeLedger();
        _ruleExists = ruleExists ?? DefaultRuleExists;
        _runNetsh = runNetsh ?? DefaultRunNetsh;
    }

    public IReadOnlyList<FirewallReport> EnsureRules(IEnumerable<FirewallRule>? rules = null)
    {
        var reports = new List<FirewallReport>();
        foreach (var rule in rules ?? DefaultRules)
        {
            if (_ruleExists(rule.Name))
            {
                reports.Add(new FirewallReport(rule.Name, true, "already present"));
                continue;
            }

            _ledger.Append("firewall-rule-add-start", $"{rule.Name} ({rule.Protocol} {rule.LocalPorts})");
            _runNetsh("add", $"advfirewall firewall add rule name=\"{rule.Name}\" dir=in action=allow protocol={rule.Protocol} localport={rule.LocalPorts} profile=any");
            var nowPresent = _ruleExists(rule.Name);
            _ledger.Append(
                nowPresent ? "firewall-rule-add-complete" : "firewall-rule-add-incomplete",
                $"{rule.Name}: present={(nowPresent ? "yes" : "no")}");
            reports.Add(new FirewallReport(rule.Name, nowPresent, nowPresent ? "added" : "add failed"));
        }

        return reports;
    }

    public IReadOnlyList<FirewallReport> RemoveRules(IEnumerable<FirewallRule>? rules = null)
    {
        var reports = new List<FirewallReport>();
        foreach (var rule in rules ?? DefaultRules)
        {
            if (!_ruleExists(rule.Name))
            {
                reports.Add(new FirewallReport(rule.Name, false, "not present"));
                continue;
            }

            _runNetsh("delete", $"advfirewall firewall delete rule name=\"{rule.Name}\"");
            _ledger.Append("firewall-rule-remove-complete", rule.Name);
            reports.Add(new FirewallReport(rule.Name, false, "removed"));
        }

        return reports;
    }

    private static bool DefaultRuleExists(string name)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = $"advfirewall firewall show rule name=\"{name}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        });
        if (process is null)
        {
            return false;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return output.Contains(name, StringComparison.OrdinalIgnoreCase);
    }

    private static void DefaultRunNetsh(string action, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start netsh.");
        }

        process.WaitForExit(15000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"netsh {action} failed with exit code {process.ExitCode}: {process.StandardError.ReadToEnd()}");
        }
    }
}
