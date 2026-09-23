namespace CODConnect.Networking;

public enum TunnelState
{
    Disconnected,
    Connecting,
    Connected,
    Failed,
}

public static class TunnelLimits
{
    public const int MaxFrameBytes = 9014;
}

public interface ITunnelTransport : IAsyncDisposable
{
    TunnelState State { get; }

    event Action<CapturedFrame>? FrameReceived;

    event Action<TunnelState>? StateChanged;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default);
}
