using System.Collections.Concurrent;
using CODConnect.Core;

namespace CODConnect.Networking;

public sealed class TunnelPortAdapter : IConsoleNetworkInterface
{
    private readonly BlockingCollection<CapturedFrame> _queue = new(4096);
    private readonly ITunnelTransport _tunnel;

    public TunnelPortAdapter(ITunnelTransport tunnel, string name, MacAddress mac)
    {
        _tunnel = tunnel;
        Name = name;
        Mac = mac;
        tunnel.FrameReceived += frame =>
        {
            if (!_queue.TryAdd(frame))
            {
                _queue.TryTake(out _);
                _queue.TryAdd(frame);
            }
        };
    }

    public string Name { get; }

    public MacAddress Mac { get; }

    public MacAddress GetMac() => Mac;

    public int GetMtu() => 1500;

    public LinkState GetLinkState()
        => _tunnel.State == TunnelState.Connected ? LinkState.Up : LinkState.Down;

    public CapturedFrame? CaptureFrame(CancellationToken cancellationToken = default)
        => _queue.TryTake(out var frame, 0, cancellationToken) ? frame : null;

    public async ValueTask InjectFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        await _tunnel.SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Dispose();
        await _tunnel.DisposeAsync().ConfigureAwait(false);
    }
}
