using Microsoft.Extensions.Hosting;

namespace CODConnect.Service;

public sealed class TransportWarmupHostedService(AutoRoomTransport transport) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            await transport.ResolveAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }
}
