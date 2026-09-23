using CODConnect.Service;
using CODConnect.Service.Firewall;
using CODConnect.Service.Prerequisites;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

FileLog.HookProcessFailures();
FileLog.Write("service", "INFO", $"starting {typeof(SessionManager).Assembly.GetName().Version}");

var wifiController = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
    ? new CODConnect.Service.Wifi.WifiRoomController(
        new CODConnect.Wifi.WindowsHotspotManager(),
        new CODConnect.Service.Wifi.NetshDhcpGuard(),
        new CODConnect.Service.Wifi.WifiJournal(),
        new CODConnect.Core.State.ChangeLedger(),
        message => FileLog.Write("wifi", "INFO", message),
        new CODConnect.Service.Wifi.NetshForwardingGuard())
    : null;

if (args.Contains("--uninstall-cleanup", StringComparer.OrdinalIgnoreCase))
{
    await UninstallCleanupAsync(wifiController);
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddProvider(new FileLoggerProvider());

if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options => options.ServiceName = "CODCONNECT");
}

builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<AutoRoomTransport>>();
    return new AutoRoomTransport(log: message => logger.LogInformation("{Message}", message), sweepOrphans: true);
});
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));

var devSettings = new DevSettings(GetRendezvousBaseUrl());
var rendezvousHttp = devSettings.Rendezvous;
var journal = new SessionJournal();
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SessionManager>>();
    return new SessionManager(
        ConsoleAdapterFactory.OpenAsync,
        sp.GetRequiredService<AutoRoomTransport>(),
        rendezvousHttp,
        log: message => logger.LogInformation("{Message}", message),
        journal: journal,
        wifi: wifiController,
        dev: devSettings);
});

builder.Services.AddHostedService(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SessionLifecycleHostedService>>();
    return new SessionLifecycleHostedService(
        sp.GetRequiredService<SessionManager>(), journal, rendezvousHttp, message => logger.LogInformation("{Message}", message), wifiController);
});
builder.Services.AddHostedService<TransportWarmupHostedService>();
builder.Services.AddSingleton<IIpcHandler>(sp => new ServiceRequestHandler(
    sp.GetRequiredService<SessionManager>(),
    ConsoleAdapterFactory.ListAdapters));
builder.Services.AddHostedService<PipeHostedService>();
builder.Services.AddPrerequisiteInstallation();
builder.Services.AddFirewallAutomation();

var host = builder.Build();
host.Run();

static async Task UninstallCleanupAsync(CODConnect.Service.Wifi.WifiRoomController? wifi)
{
    void Log(string message) => FileLog.Write("uninstall", "INFO", message);
    async Task Step(string name, Func<Task> action)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await action().WaitAsync(timeout.Token);
            Log($"{name}: done");
        }
        catch (Exception ex)
        {
            Log($"{name}: {ex.Message}");
        }
    }

    using var rendezvous = new HttpClient { BaseAddress = new Uri(new DevSettings(GetRendezvousBaseUrl()).RoomServer), Timeout = TimeSpan.FromSeconds(10) };
    await Step("room left open", () => new SessionJournal().RecoverAsync(rendezvous, Log));
    if (wifi is not null)
    {
        await Step("Wi-Fi hotspot, setting and DHCP guard", () => wifi.StopAsync());
    }

    await Step("SoftEther room hubs and connections", () => new AutoRoomTransport(Log, sweepOrphans: true).ResolveAsync());
    await Step("firewall rules", () => Task.Run(() => new CODConnect.Service.Firewall.FirewallManager().RemoveRules()));
}

static string GetRendezvousBaseUrl()
{
    var configured = Environment.GetEnvironmentVariable("CODCONNECT_RENDEZVOUS_URL");
    return string.IsNullOrWhiteSpace(configured) ? "http://127.0.0.1:5090/" : configured;
}
