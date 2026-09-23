namespace CODConnect.Core;

public interface IPeerAwarePort
{
    LinkState TransportLinkState { get; }
}

public interface IConsoleNetworkInterface : IAsyncDisposable
{
    string Name { get; }

    MacAddress GetMac();

    int GetMtu();

    LinkState GetLinkState();

    CapturedFrame? CaptureFrame(CancellationToken cancellationToken = default);

    ValueTask InjectFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default);
}
