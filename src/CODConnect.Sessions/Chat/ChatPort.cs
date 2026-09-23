namespace CODConnect.Sessions.Chat;

internal sealed class ChatPort(IConsoleNetworkInterface inner, Action<ReadOnlyMemory<byte>> receive) : IConsoleNetworkInterface, IPeerAwarePort
{
    public string Name => inner.Name;
    public MacAddress GetMac() => inner.GetMac();
    public int GetMtu() => inner.GetMtu();
    public LinkState GetLinkState() => inner.GetLinkState();
    public LinkState TransportLinkState => inner is IPeerAwarePort peer ? peer.TransportLinkState : inner.GetLinkState();
    internal static bool IsChat(ReadOnlySpan<byte> frame) => frame.Length >= 14 && frame[12] == 0x88 && frame[13] == 0xB5;
    public CapturedFrame? CaptureFrame(CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < 32; i++)
        {
            var frame = inner.CaptureFrame(cancellationToken);
            if (frame is null || !IsChat(frame.Value.Buffer.Span)) return frame;
            receive(frame.Value.Buffer);
        }
        return null;
    }
    public ValueTask InjectFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        => IsChat(frame.Span) ? ValueTask.CompletedTask : inner.InjectFrameAsync(frame, cancellationToken);
    internal ValueTask SendChatAsync(ReadOnlyMemory<byte> frame, CancellationToken token) => inner.InjectFrameAsync(frame, token);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
