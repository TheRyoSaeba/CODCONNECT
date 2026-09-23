using CODConnect.Core.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CODConnect.Service.Firewall;

internal sealed class FirewallHostedService : BackgroundService
{
    private readonly ILogger<FirewallHostedService> _logger;

    public FirewallHostedService(ILogger<FirewallHostedService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Task.Run(() =>
        {
            var manager = new FirewallManager(new ChangeLedger());
            foreach (var report in manager.EnsureRules())
            {
                _logger.LogInformation("Firewall {Rule}: {Action}", report.Rule, report.Action);
            }
        }, stoppingToken).ConfigureAwait(false);
    }
}

public static class FirewallServiceCollectionExtensions
{
    public static IServiceCollection AddFirewallAutomation(this IServiceCollection services)
    {
        services.AddHostedService<FirewallHostedService>();
        return services;
    }
}
