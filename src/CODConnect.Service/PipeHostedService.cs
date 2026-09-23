using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CODConnect.Service;

internal sealed class PipeHostedService : BackgroundService
{
    private readonly IIpcHandler _handler;
    private readonly ILogger<PipeHostedService> _logger;
    private ServicePipeServer? _server;

    public PipeHostedService(IIpcHandler handler, ILogger<PipeHostedService> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _server = new ServicePipeServer(
            _handler,
            pipeName: null,
            log: message => _logger.LogWarning("{Message}", message));
        return _server.StartAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
