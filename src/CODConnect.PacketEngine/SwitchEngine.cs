using System.Collections.Concurrent;
using CODConnect.Core;
using CODConnect.Core.Diagnostics;

namespace CODConnect.PacketEngine;

public sealed class SwitchEngine : IAsyncDisposable
{
    private readonly UserSpaceSwitch _switch;
    private readonly NetworkCounters? _counters;
    private readonly ConcurrentDictionary<int, IConsoleNetworkInterface> _ports = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SwitchEngine(UserSpaceSwitch sw, NetworkCounters? counters = null)
    {
        _switch = sw ?? throw new ArgumentNullException(nameof(sw));
        _counters = counters;
        _switch.FrameForwarded += OnFrameForwarded;
    }

    public int AddPort(IConsoleNetworkInterface iface, bool isConsolePort)
    {
        ArgumentNullException.ThrowIfNull(iface);
        var portId = _switch.AddPort(iface.Name, iface);
        _ports[portId] = iface;
        if (isConsolePort)
        {
            _switch.MarkConsolePort(portId);
        }

        return portId;
    }

    public int AddGatewayPort(IConsoleNetworkInterface iface)
    {
        ArgumentNullException.ThrowIfNull(iface);
        var portId = _switch.AddPort(iface.Name, iface);
        _switch.MarkGatewayPort(portId);
        _ports[portId] = iface;
        return portId;
    }

    public bool RemovePort(int portId)
    {
        return _ports.TryRemove(portId, out _) && _switch.RemovePort(portId);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return Task.CompletedTask;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;
            _loop = Task.Run(() => PollLoopAsync(token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            loop = _loop;
            _loop = null;
            _cts?.Cancel();
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _switch.FrameForwarded -= OnFrameForwarded;
        await StopAsync().ConfigureAwait(false);
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var capturedAny = false;
                foreach (var (portId, iface) in _ports)
                {
                    CapturedFrame? frame;
                    try
                    {
                        frame = iface.CaptureFrame(token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        _counters?.RecordDropped();
                        continue;
                    }

                    if (frame is null)
                    {
                        continue;
                    }

                    capturedAny = true;
                    _switch.HandleFrame(portId, frame.Value);
                }

                if (!capturedAny)
                {
                    await Task.Delay(1, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnFrameForwarded(int egressPortId, CapturedFrame frame)
    {
        if (!_ports.TryGetValue(egressPortId, out var port))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await port.InjectFrameAsync(frame.Buffer, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                _counters?.RecordDropped();
            }
        });
    }
}
