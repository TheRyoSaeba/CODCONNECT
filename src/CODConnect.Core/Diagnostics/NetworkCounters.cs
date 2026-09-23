namespace CODConnect.Core.Diagnostics;

public readonly record struct NetworkCounterSnapshot(
    long FramesCaptured,
    long FramesInjected,
    long BroadcastFramesIn,
    long BroadcastFramesOut,
    long UnicastFramesIn,
    long UnicastFramesOut,
    long FramesDropped,
    long MalformedFrames,
    long BytesIn,
    long BytesOut);

public sealed class NetworkCounters
{
    private long _framesCaptured;
    private long _framesInjected;
    private long _broadcastFramesIn;
    private long _broadcastFramesOut;
    private long _unicastFramesIn;
    private long _unicastFramesOut;
    private long _framesDropped;
    private long _malformedFrames;
    private long _bytesIn;
    private long _bytesOut;

    public long FramesCaptured => Interlocked.Read(ref _framesCaptured);
    public long FramesInjected => Interlocked.Read(ref _framesInjected);
    public long BroadcastFramesIn => Interlocked.Read(ref _broadcastFramesIn);
    public long BroadcastFramesOut => Interlocked.Read(ref _broadcastFramesOut);
    public long UnicastFramesIn => Interlocked.Read(ref _unicastFramesIn);
    public long UnicastFramesOut => Interlocked.Read(ref _unicastFramesOut);
    public long FramesDropped => Interlocked.Read(ref _framesDropped);
    public long MalformedFrames => Interlocked.Read(ref _malformedFrames);
    public long BytesIn => Interlocked.Read(ref _bytesIn);
    public long BytesOut => Interlocked.Read(ref _bytesOut);

    public void RecordCaptured(int bytes, bool broadcast)
    {
        Interlocked.Increment(ref _framesCaptured);
        Interlocked.Add(ref _bytesIn, bytes);
        if (broadcast)
        {
            Interlocked.Increment(ref _broadcastFramesIn);
        }
        else
        {
            Interlocked.Increment(ref _unicastFramesIn);
        }
    }

    public void RecordInjected(int bytes, bool broadcast)
    {
        Interlocked.Increment(ref _framesInjected);
        Interlocked.Add(ref _bytesOut, bytes);
        if (broadcast)
        {
            Interlocked.Increment(ref _broadcastFramesOut);
        }
        else
        {
            Interlocked.Increment(ref _unicastFramesOut);
        }
    }

    public void RecordDropped() => Interlocked.Increment(ref _framesDropped);

    public void RecordMalformed() => Interlocked.Increment(ref _malformedFrames);

    public NetworkCounterSnapshot Snapshot() => new(
        FramesCaptured,
        FramesInjected,
        BroadcastFramesIn,
        BroadcastFramesOut,
        UnicastFramesIn,
        UnicastFramesOut,
        FramesDropped,
        MalformedFrames,
        BytesIn,
        BytesOut);
}
