using System.Collections.Concurrent;
using CODConnect.Core;

namespace CODConnect.NetworkSimulation;

public sealed class VirtualPairLink : IConsoleNetworkInterface
{
    private readonly ConcurrentQueue<CapturedFrame> _inbound = new();
    private VirtualPairLink? _peer;
    private LinkState _linkState = LinkState.Up;

    private VirtualPairLink(string name, MacAddress mac)
    {
        Name = name;
        Mac = mac;
    }

    public string Name { get; }

    public MacAddress Mac { get; }

    public int Mtu => 1500;

    public static (VirtualPairLink A, VirtualPairLink B) CreatePair(string nameA, string nameB, MacAddress macA, MacAddress macB)
    {
        var a = new VirtualPairLink(nameA, macA);
        var b = new VirtualPairLink(nameB, macB);
        a._peer = b;
        b._peer = a;
        return (a, b);
    }

    public MacAddress GetMac() => Mac;

    public int GetMtu() => Mtu;

    public LinkState GetLinkState() => _linkState;

    public CapturedFrame? CaptureFrame(CancellationToken cancellationToken = default)
        => _inbound.TryDequeue(out var frame) ? frame : null;

    public ValueTask InjectFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        if (_linkState == LinkState.Down || _peer is null || _peer._linkState == LinkState.Down)
        {
            return ValueTask.CompletedTask;
        }

        _peer._inbound.Enqueue(new CapturedFrame(frame.ToArray(), DateTimeOffset.UtcNow));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _linkState = LinkState.Down;
        return ValueTask.CompletedTask;
    }
}
