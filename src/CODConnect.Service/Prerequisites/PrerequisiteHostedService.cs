using CODConnect.Core.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CODConnect.Service.Prerequisites;

internal sealed class PrerequisiteHostedService : BackgroundService
{
    private readonly ILogger<PrerequisiteHostedService> _logger;

    public PrerequisiteHostedService(ILogger<PrerequisiteHostedService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var manager = new PrerequisiteManager(ledger: new ChangeLedger());
        await Task.Run(() =>
        {
            foreach (var report in manager.EnsureInstalled())
            {
                if (report.Installed)
                {
                    _logger.LogInformation("Prerequisite {Name}: {Detail}", report.Name, report.Detail);
                }
                else
                {
                    _logger.LogWarning("Prerequisite {Name}: {Detail}", report.Name, report.Detail);
                }
            }
        }, stoppingToken).ConfigureAwait(false);
    }
}

public static class PrerequisiteServiceCollectionExtensions
{
    public static IServiceCollection AddPrerequisiteInstallation(this IServiceCollection services)
    {
        services.AddHostedService<PrerequisiteHostedService>();
        return services;
    }
}
