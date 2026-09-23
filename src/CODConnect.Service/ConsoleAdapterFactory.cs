using CODConnect.PacketEngine;

namespace CODConnect.Service;

public static class ConsoleAdapterFactory
{
    public static Task<IConsoleNetworkInterface> OpenAsync(string? selector)
        => OpenAsync(selector, allowInternetAdapter: false);

    public static Task<IConsoleNetworkInterface> OpenAsync(string? selector, bool allowInternetAdapter)
        => Task.FromResult<IConsoleNetworkInterface>(Open(selector, allowInternetAdapter));

    public static IConsoleNetworkInterface Open(string? selector, bool allowInternetAdapter = false)
    {
        var adapter = Resolve(selector, allowInternetAdapter);
        return new NpcapConsoleInterface(adapter);
    }

    public static IReadOnlyList<IpcAdapterInfo> ListAdapters()
    {
        if (!PcapAdapters.IsAvailable)
        {
            return [];
        }

        var internet = InternetAdapterIds();
        return PcapAdapters.Enumerate()
            .Select((a, index) => (Adapter: a, Index: index))
            .Where(x => !x.Adapter.FriendlyName.Contains("VPN Client", StringComparison.OrdinalIgnoreCase))
            .Select(x =>
            {
                var a = x.Adapter;
                var carriesInternet = CarriesInternet(a, internet);
                var kind = Classify(a);
                return new IpcAdapterInfo(
                    Index: x.Index,
                    Name: a.FriendlyName,
                    Mac: a.Mac.ToString(),
                    Addresses: a.Addresses.Select(ip => ip.ToString()).ToList(),
                    LinkState: a.LinkState.ToString(),
                    IsLoopback: a.IsLoopback,
                    CarriesInternet: carriesInternet,
                    Kind: kind,
                    Recommended: kind == "Ethernet" && !carriesInternet && !a.IsLoopback && a.LinkState == LinkState.Up);
            })
            .ToList();
    }

    private static string Classify(PcapAdapterInfo adapter)
    {
        if (adapter.IsLoopback)
        {
            return "Other";
        }

        if (IsVirtualByName(adapter))
        {
            return adapter.FriendlyName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ? "Other" : "Virtual";
        }

        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!adapter.PcapName.Contains(nic.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return nic.NetworkInterfaceType switch
                {
                    System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
                        or System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet
                        or System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetT
                        or System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetFx => "Ethernet",
                    System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    _ => "Other",
                };
            }
        }
        catch
        {
        }

        return "Other";
    }

    private static bool IsVirtualByName(PcapAdapterInfo adapter)
        => adapter.FriendlyName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
           || adapter.FriendlyName.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase)
           || adapter.FriendlyName.Contains("VMware", StringComparison.OrdinalIgnoreCase)
           || adapter.FriendlyName.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
           || adapter.FriendlyName.Contains("VPN Client", StringComparison.OrdinalIgnoreCase)
           || adapter.FriendlyName.StartsWith("Local Area Connection*", StringComparison.OrdinalIgnoreCase);

    private static bool CarriesInternet(PcapAdapterInfo adapter, HashSet<string> internetAdapterIds)
        => internetAdapterIds.Any(id => adapter.PcapName.Contains(id, StringComparison.OrdinalIgnoreCase));

    private static PcapAdapterInfo Resolve(string? selector, bool allowInternetAdapter)
    {
        if (!PcapAdapters.IsAvailable)
        {
            throw new InvalidOperationException(
                "Npcap is not installed on this machine; install it or use the bundled installer (plan addendum 29.6).");
        }

        var adapters = PcapAdapters.Enumerate();
        if (adapters.Count == 0)
        {
            throw new InvalidOperationException("No capture-able network adapters were found.");
        }

        var internetIds = InternetAdapterIds();
        return SelectAdapter(
            adapters,
            selector,
            allowInternetAdapter,
            isInternet: a => CarriesInternet(a, internetIds),
            isEthernet: IsPhysicalEthernet);
    }

    public static PcapAdapterInfo SelectAdapter(
        IReadOnlyList<PcapAdapterInfo> adapters,
        string? selector,
        bool allowInternetAdapter,
        Func<PcapAdapterInfo, bool> isInternet,
        Func<PcapAdapterInfo, bool> isEthernet)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            var candidates = adapters
                .Where(a => a.LinkState == LinkState.Up && !a.IsLoopback && !IsVirtualByName(a) && !isInternet(a))
                .ToList();
            return candidates.FirstOrDefault(isEthernet)
                   ?? candidates.FirstOrDefault()
                   ?? throw new InvalidOperationException(
                       "No spare connected adapter is available (this PC's Internet adapter and virtual adapters are excluded). "
                       + "Choose the port your console is plugged into.");
        }

        PcapAdapterInfo chosen;
        if (int.TryParse(selector, out var index))
        {
            chosen = index >= 0 && index < adapters.Count
                ? adapters[index]
                : throw new ArgumentException($"Adapter index {index} is out of range (0..{adapters.Count - 1}).", nameof(selector));
        }
        else
        {
            chosen = adapters.FirstOrDefault(a => a.FriendlyName.Equals(selector, StringComparison.OrdinalIgnoreCase))
                     ?? adapters.FirstOrDefault(a =>
                         a.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase)
                         || a.PcapName.Contains(selector, StringComparison.OrdinalIgnoreCase))
                     ?? throw new ArgumentException($"No adapter matches '{selector}'.", nameof(selector));
        }

        if (!allowInternetAdapter && isInternet(chosen))
        {
            throw new InternetAdapterRefusedException(chosen.FriendlyName);
        }

        return chosen;
    }

    private static bool IsPhysicalEthernet(PcapAdapterInfo adapter)
    {
        if (adapter.FriendlyName.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase)
            || adapter.FriendlyName.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || adapter.FriendlyName.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || adapter.FriendlyName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
            || adapter.FriendlyName.StartsWith("Local Area Connection*", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
                adapter.PcapName.Contains(nic.Id, StringComparison.OrdinalIgnoreCase)
                && nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
                    or System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet
                    or System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetT
                    or System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetFx);
        }
        catch
        {
            return false;
        }
    }

    public sealed class InternetAdapterRefusedException(string adapterName)
        : InvalidOperationException(
            $"'{adapterName}' is carrying this PC's Internet connection. CODCONNECT takes the console port over completely, "
            + "so using it would disconnect this PC and expose your home network's traffic to the room. "
            + "Pick the spare port your console is plugged into, or confirm to use it anyway.")
    {
        public string AdapterName { get; } = adapterName;
    }

    private static bool IsConsoleCandidate(PcapAdapterInfo adapter, HashSet<string> internetAdapterIds)
        => !adapter.IsLoopback
           && !adapter.FriendlyName.Contains("VPN Client", StringComparison.OrdinalIgnoreCase)
           && !internetAdapterIds.Any(id => adapter.PcapName.Contains(id, StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> InternetAdapterIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    continue;
                }

                if (nic.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                {
                    ids.Add(nic.Id);
                }
            }
        }
        catch
        {
        }

        return ids;
    }
}
