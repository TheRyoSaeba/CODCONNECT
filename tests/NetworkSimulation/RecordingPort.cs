using System.Collections.Concurrent;
using CODConnect.Core;

namespace CODConnect.NetworkSimulation;

public sealed class RecordingPort : IConsoleNetworkInterface
{
    private readonly ConcurrentQueue<CapturedFrame> _pending = new();
    private readonly object _injectedLock = new();
    private readonly List<byte[]> _injected = new();
    private LinkState _linkState = LinkState.Up;

    public RecordingPort(string name, MacAddress mac)
    {
        Name = name;
        Mac = mac;
    }

    public string Name { get; }

    public MacAddress Mac { get; }

    public int Mtu => 1500;

    public void EnqueueFrame(ReadOnlySpan<byte> frame)
        => _pending.Enqueue(new CapturedFrame(frame.ToArray(), DateTimeOffset.UtcNow));

    public IReadOnlyList<byte[]> SnapshotInjected()
    {
        lock (_injectedLock)
        {
            return _injected.Select(f => f.ToArray()).ToArray();
        }
    }

    public MacAddress GetMac() => Mac;

    public int GetMtu() => Mtu;

    public LinkState GetLinkState() => _linkState;

    public CapturedFrame? CaptureFrame(CancellationToken cancellationToken = default)
        => _pending.TryDequeue(out var frame) ? frame : null;

    public ValueTask InjectFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        lock (_injectedLock)
        {
            _injected.Add(frame.ToArray());
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _linkState = LinkState.Down;
        return ValueTask.CompletedTask;
    }
}
