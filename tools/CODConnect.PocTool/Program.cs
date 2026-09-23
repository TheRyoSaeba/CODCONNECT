using System.Text;
using System.Security.Principal;
using CODConnect.Core;
using CODConnect.Core.Diagnostics;
using CODConnect.Core.Rooms;
using CODConnect.Networking;
using CODConnect.PacketEngine;
using CODConnect.PacketEngine.Dhcp;
using CODConnect.PocTool;

if (args.Length == 0)
{
    return PrintUsage();
}

return args[0] switch
{
    "doctor" => await DoctorAsync(),
    "list-adapters" => ListAdapters(),
    "sniff" => await SniffAsync(args[1..]),
    "inject-test" => await InjectTestAsync(args[1..]),
    "bridge" => await BridgeAsync(args[1..]),
    "pipe-listen" => await PipeAsync(args[1..], listen: true),
    "pipe-join" => await PipeAsync(args[1..], listen: false),
    "service" => await ServiceAsync(args[1..]),
    "wifi" => await WifiAsync(args[1..]),
    _ => PrintUsage(),
};

static async Task<int> WifiAsync(string[] args)
{
    var manager = new CODConnect.Wifi.WindowsHotspotManager();
    var report = await manager.CheckCapabilityAsync();
    Console.WriteLine($"Wi-Fi adapter : {report.AdapterDescription ?? "(none)"}");
    Console.WriteLine($"Current link  : {(report.Channel is null ? "not connected" : $"{report.Band}, channel {report.Channel}")}");
    Console.WriteLine($"Supported     : {report.Supported} - {report.Reason}");
    foreach (var warning in report.Warnings ?? [])
    {
        Console.WriteLine($"Warning       : {warning}");
    }

    if (args.Length == 0 || args[0] != "host" || !report.Supported)
    {
        return report.Supported ? 0 : 1;
    }

    var seconds = args.Length > 2 && args[1] == "--seconds" && int.TryParse(args[2], out var s) ? s : 120;
    var original = (await manager.GetStatusAsync()).Settings;
    var room = new CODConnect.Wifi.HotspotSettings(
        "CODCONNECT-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(2)),
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
        "2.4 GHz");
    try
    {
        var hotspot = await manager.StartHotspotAsync(room);
        Console.WriteLine($"Hotspot       : SSID {hotspot.Settings.Ssid}  password {hotspot.Settings.Passphrase}  band {hotspot.Settings.Band}");
        Console.WriteLine($"Adapter       : {hotspot.AdapterName} (Windows address {hotspot.WindowsAddress ?? "none"})");
        Console.WriteLine($"Holding for {seconds} s...");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
    }
    finally
    {
        await manager.StopHotspotAsync();
        await manager.ApplySettingsAsync(original);
        Console.WriteLine("Hotspot stopped; your own hotspot settings restored.");
    }

    return 0;
}

static async Task<int> ServiceAsync(string[] args)
{
    var op = args.Length > 0 ? args[0] : "status";
    var client = new CODConnect.Protocol.ServiceIpcClient();
    var request = op switch
    {
        "host" => new CODConnect.Protocol.IpcRequest("host", DisplayName: "PocTool", Adapter: args.Length > 1 ? args[1] : null),
        "host-wifi" => new CODConnect.Protocol.IpcRequest("host", DisplayName: "PocTool", Mode: "wifi"),
        "join" => new CODConnect.Protocol.IpcRequest("join", DisplayName: "PocTool", RoomCode: args.Length > 1 ? args[1] : null, Adapter: args.Length > 2 ? args[2] : null),
        _ => new CODConnect.Protocol.IpcRequest(op),
    };

    try
    {
        var response = await client.SendAsync(request);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(response, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return response.Ok ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"service unreachable: {ex.GetType().Name}: {ex.Message}");
        return 2;
    }
}

static int PrintUsage()
{
    Console.WriteLine("""
        CODCONNECT developer tool

        Usage:
          doctor                          Check Npcap, privileges, adapters
          list-adapters                   Show capture-able adapters
          sniff <adapter> [options]       Print live frames (use --count N, --filter <bpf>)
          inject-test <adapter> [--yes]   Inject ARP + UDP broadcast frames and verify capture
          bridge --console A --uplink B   User-space switch between two local adapters
                 [--dhcp]                 Run the room DHCP responder on the console port
          pipe-listen --port N --console A [--dhcp]
                                          Host a virtual cable over TCP, bridge to adapter A
          pipe-join --host H --port N --console A [--dhcp]
                                          Join a hosted virtual cable

        <adapter> is an index from list-adapters or a name substring.
        Prefer the 'Npcap Loopback Adapter' for inject-test: it is fully self-contained.
        """);
    return 0;
}

static bool IsElevated()
    => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

static async Task<int> DoctorAsync()
{
    Console.WriteLine("CODCONNECT doctor");
    Console.WriteLine();
    Console.WriteLine($" Elevated process      : {(IsElevated() ? "yes" : "no")}");
    Console.WriteLine($" OS                    : {Environment.OSVersion.VersionString}");
    Console.WriteLine($" .NET                  : {Environment.Version}");

    if (!PcapAdapters.IsAvailable)
    {
        Console.WriteLine(" Npcap                 : NOT INSTALLED");
        Console.WriteLine();
        Console.WriteLine(" Install Npcap from https://npcap.com (keep default options), then re-run.");
        return 1;
    }

    Console.WriteLine(" Npcap                 : available");

    IReadOnlyList<PcapAdapterInfo> adapters;
    try
    {
        adapters = PcapAdapters.Enumerate();
    }
    catch (Exception ex)
    {
        Console.WriteLine($" Npcap                 : present but enumeration failed ({ex.Message})");
        return 1;
    }

    Console.WriteLine($" Capture-able adapters : {adapters.Count}");
    Console.WriteLine();
    PrintAdapters(adapters);

    if (!IsElevated())
    {
        Console.WriteLine(" Note: if an adapter fails to open, Npcap may have been installed with");
        Console.WriteLine("       'Admin Only'; re-run this tool elevated in that case.");
    }

    await Task.CompletedTask;
    return 0;
}

static int ListAdapters()
{
    if (!PcapAdapters.IsAvailable)
    {
        Console.WriteLine("Npcap is not installed. Run 'doctor' for details.");
        return 1;
    }

    PrintAdapters(PcapAdapters.Enumerate());
    return 0;
}

static void PrintAdapters(IReadOnlyList<PcapAdapterInfo> adapters)
{
    Console.WriteLine($"{"IDX",-4} {"LINK",-5} {"LOOP",-5} {"MAC",-18} {"ADDRESSES",-30} NAME");
    for (var i = 0; i < adapters.Count; i++)
    {
        var a = adapters[i];
        var link = a.LinkState == LinkState.Up ? "up" : "down";
        var loop = a.IsLoopback ? "yes" : "";
        var ips = string.Join(",", a.Addresses.Select(ip => ip.ToString()));
        if (ips.Length == 0)
        {
            ips = "-";
        }

        var name = a.FriendlyName;
        if (name.Length > 60)
        {
            name = name[..60];
        }

        Console.WriteLine($"{i,-4} {link,-5} {loop,-5} {a.Mac,-18} {ips,-30} {name}");
    }
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--"))
        {
            var key = args[i][2..];
            if (key.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || key.Equals("dhcp", StringComparison.OrdinalIgnoreCase))
            {
                options[key] = "true";
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                options[key] = args[++i];
            }
            else
            {
                options[key] = "true";
            }
        }
    }

    return options;
}

static async Task<int> SniffAsync(string[] args)
{
    var selector = args.FirstOrDefault(a => !a.StartsWith("--"));
    var options = ParseOptions(args);
    var adapter = AdapterSelector.Resolve(selector, out var error);
    if (adapter is null)
    {
        Console.WriteLine(error);
        return 2;
    }

    options.TryGetValue("filter", out var filter);
    var limit = options.TryGetValue("count", out var countValue) ? int.Parse(countValue) : int.MaxValue;

    Console.WriteLine($"Sniffing '{adapter.FriendlyName}'{(filter is null ? "" : $" with filter '{filter}'")} (Ctrl+C to stop)...");
    var counters = new NetworkCounters();
    await using var iface = new NpcapConsoleInterface(adapter, counters, filter);

    var seen = 0;
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancellation.Cancel();
    };
    if (options.TryGetValue("seconds", out var seconds))
    {
        cancellation.CancelAfter(TimeSpan.FromSeconds(int.Parse(seconds)));
    }

    using var pcap = options.TryGetValue("out", out var outPath) ? new BinaryWriter(File.Create(outPath)) : null;
    pcap?.Write(0xA1B2C3D4u); pcap?.Write((ushort)2); pcap?.Write((ushort)4); pcap?.Write(0); pcap?.Write(0u); pcap?.Write(65535u); pcap?.Write(1u);

    while (seen < limit && !cancellation.IsCancellationRequested)
    {
        CapturedFrame? frame = null;
        try
        {
            frame = iface.CaptureFrame(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }

        if (frame is null)
        {
            await Task.Delay(1, CancellationToken.None);
            continue;
        }

        Console.WriteLine(FrameFormatter.Format(frame.Value.Buffer.Span));
        if (pcap is not null)
        {
            var at = frame.Value.Timestamp.ToUnixTimeMilliseconds();
            pcap.Write((uint)(at / 1000)); pcap.Write((uint)(at % 1000 * 1000));
            pcap.Write((uint)frame.Value.Buffer.Length); pcap.Write((uint)frame.Value.Buffer.Length);
            pcap.Write(frame.Value.Buffer.Span);
            pcap.Flush();
        }

        seen++;
    }

    Console.WriteLine();
    Console.WriteLine($"Captured {seen} frames. {counters.Snapshot()}");
    return 0;
}

static async Task<int> InjectTestAsync(string[] args)
{
    var selector = args.FirstOrDefault(a => !a.StartsWith("--"));
    var options = ParseOptions(args);
    var adapter = AdapterSelector.Resolve(selector, out var error);
    if (adapter is null)
    {
        Console.WriteLine(error);
        return 2;
    }

    if (!adapter.IsLoopback && !options.ContainsKey("yes"))
    {
        Console.WriteLine($"'{adapter.FriendlyName}' is a physical adapter: inject-test will emit two broadcast");
        Console.WriteLine("frames onto that network. Re-run with --yes to accept, or use the Npcap Loopback Adapter.");
        return 2;
    }

    Console.WriteLine($"Opening '{adapter.FriendlyName}'...");
    var counters = new NetworkCounters();
    await using var iface = new NpcapConsoleInterface(adapter, counters);

    var sourceMac = new MacAddress(0x02, 0xCD, 0x00, 0x00, 0x00, 0x01);
    var arpFrame = ArpPacket.BuildEthernetFrame(sourceMac, new IPv4Address(10, 42, 0, 77), new IPv4Address(10, 42, 0, 99), isReply: false, replyDestinationMac: MacAddress.None);
    var udpPayload = Encoding.ASCII.GetBytes("CODCONNECT-INJECT-TEST");
    var udpFrame = EthernetFrame.Build(
        MacAddress.Broadcast,
        sourceMac,
        EthernetFrame.EtherTypeIpv4,
        IPv4Packet.BuildPayload(
            new IPv4Address(10, 42, 0, 77),
            IPv4Address.Broadcast,
            IPv4Packet.ProtocolUdp,
            UdpPacket.BuildPayload(42424, 42424, udpPayload, computeChecksum: false)));

    Console.WriteLine($"Injecting ARP request      : {FrameFormatter.Format(arpFrame)}");
    await iface.InjectFrameAsync(arpFrame);
    Console.WriteLine($"Injecting UDP broadcast    : {FrameFormatter.Format(udpFrame)}");
    await iface.InjectFrameAsync(udpFrame);

    Console.WriteLine("Capturing for 2 seconds to verify echo...");
    var echoes = 0;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
    while (DateTimeOffset.UtcNow < deadline)
    {
        var frame = iface.CaptureFrame();
        if (frame is null)
        {
            await Task.Delay(1);
            continue;
        }

        if (frame.Value.Buffer.Span.SequenceEqual(arpFrame) || frame.Value.Buffer.Span.SequenceEqual(udpFrame))
        {
            echoes++;
            Console.WriteLine($"  echo #{echoes}: {FrameFormatter.Format(frame.Value.Buffer.Span)}");
        }
    }

    Console.WriteLine();
    Console.WriteLine(echoes >= 2
        ? "PASS: frames were injected and captured back. Capture + injection works on this adapter."
        : $"ECHOES OBSERVED: {echoes}. Injection may not loop back on this adapter type; check sniff output.");
    return echoes >= 2 ? 0 : 1;
}

static async Task<int> BridgeAsync(string[] args)
{
    var options = ParseOptions(args);
    options.TryGetValue("console", out var consoleSelector);
    options.TryGetValue("uplink", out var uplinkSelector);

    var consoleAdapter = AdapterSelector.Resolve(consoleSelector, out var consoleError);
    var uplinkAdapter = AdapterSelector.Resolve(uplinkSelector, out var uplinkError);
    if (consoleAdapter is null)
    {
        Console.WriteLine(consoleError);
        return 2;
    }

    if (uplinkAdapter is null)
    {
        Console.WriteLine(uplinkError);
        return 2;
    }

    await using var uplinkIface = new NpcapConsoleInterface(uplinkAdapter, null);
    return await RunSwitchedSessionAsync(consoleAdapter, uplinkIface, options, "local bridge");
}

static async Task<int> PipeAsync(string[] args, bool listen)
{
    var options = ParseOptions(args);
    options.TryGetValue("console", out var consoleSelector);
    var consoleAdapter = AdapterSelector.Resolve(consoleSelector, out var consoleError);
    if (consoleAdapter is null)
    {
        Console.WriteLine(consoleError);
        return 2;
    }

    ITunnelTransport tunnel;
    if (listen)
    {
        if (!options.TryGetValue("port", out var portValue) || !int.TryParse(portValue, out var port))
        {
            Console.WriteLine("--port is required for pipe-listen.");
            return 2;
        }

        tunnel = new TcpTunnelTransport(new TcpTunnelOptions { ListenPort = port });
        Console.WriteLine($"Listening for a peer on TCP port {port}...");
    }
    else
    {
        if (!options.TryGetValue("host", out var host)
            || !options.TryGetValue("port", out var portValue)
            || !int.TryParse(portValue, out var port))
        {
            Console.WriteLine("--host and --port are required for pipe-join.");
            return 2;
        }

        tunnel = new TcpTunnelTransport(new TcpTunnelOptions { ConnectHost = host, ConnectPort = port });
        Console.WriteLine($"Connecting to {host}:{port}...");
    }

    await tunnel.StartAsync();
    Console.WriteLine($"Tunnel state: {tunnel.State}");

    var tunnelMac = listen
        ? new MacAddress(0x02, 0xCD, 0x00, 0x00, 0x00, 0x01)
        : new MacAddress(0x02, 0xCD, 0x00, 0x00, 0x00, 0x02);
    await using var tunnelPort = new TunnelPortAdapter(tunnel, "pipe", tunnelMac);
    return await RunSwitchedSessionAsync(consoleAdapter, tunnelPort, options, listen ? "pipe host" : "pipe join");
}

static async Task<int> RunSwitchedSessionAsync(
    PcapAdapterInfo consoleAdapterInfo,
    IConsoleNetworkInterface uplink,
    IReadOnlyDictionary<string, string> options,
    string sessionLabel)
{
    var counters = new NetworkCounters();
    await using var consoleIface = new NpcapConsoleInterface(consoleAdapterInfo, counters);
    Console.WriteLine($"[{sessionLabel}] console : {consoleAdapterInfo.FriendlyName}");
    Console.WriteLine($"[{sessionLabel}] uplink  : {uplink.Name}");
    Console.WriteLine($"[{sessionLabel}] Ctrl+C to stop. Counters print every 5 seconds.");

    var sw = new UserSpaceSwitch(new SwitchOptions { StrictConsoleScoping = true }, counters);
    var engine = new SwitchEngine(sw);
    engine.AddPort(consoleIface, isConsolePort: true);
    engine.AddPort(uplink, isConsolePort: false);

    DhcpServer? dhcp = null;
    var sessionPorts = new List<IConsoleNetworkInterface> { consoleIface, uplink };
    if (options.ContainsKey("dhcp"))
    {
        dhcp = new DhcpServer(new RoomOptions(), counters);
        dhcp.LeaseGranted += (mac, ip, hostname) =>
            Console.WriteLine($"[dhcp] leased {ip} to {mac} ({(string.IsNullOrEmpty(hostname) ? "no hostname" : hostname)})");
        sw.FrameForwarded += (_, frame) => TryServeDhcp(dhcp, frame, sessionPorts);
        Console.WriteLine($"[{sessionLabel}] DHCP responder on {new RoomOptions().ServerAddress}");
    }

    await engine.StartAsync();
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancellation.Cancel();
    };

    try
    {
        while (!cancellation.IsCancellationRequested)
        {
            await Task.Delay(5000, cancellation.Token).ConfigureAwait(true);
            Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] {counters.Snapshot()}");
        }
    }
    catch (OperationCanceledException)
    {
    }

    await engine.StopAsync();
    Console.WriteLine($"[{sessionLabel}] stopped. {counters.Snapshot()}");
    return 0;
}

static void TryServeDhcp(DhcpServer dhcp, CapturedFrame frame, IReadOnlyList<IConsoleNetworkInterface> sessionPorts)
{
    try
    {
        if (!EthernetFrame.TryParse(frame.Buffer.Span, out var parsed)
            || parsed.EtherType != EthernetFrame.EtherTypeIpv4
            || !IPv4Packet.TryParse(parsed.Payload, out var ip)
            || ip.Protocol != IPv4Packet.ProtocolUdp
            || !UdpPacket.TryParse(ip.Payload, out var udp)
            || udp.DestinationPort != UdpPacket.PortDhcpServer)
        {
            return;
        }

        var replies = dhcp.HandleFrame(frame, sessionPorts[0].GetMac());
        foreach (var reply in replies)
        {
            foreach (var port in sessionPorts)
            {
                port.InjectFrameAsync(reply).AsTask().GetAwaiter().GetResult();
            }

            Console.WriteLine($"[dhcp] reply injected: {FrameFormatter.Format(reply)}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[dhcp] error: {ex.Message}");
    }
}
