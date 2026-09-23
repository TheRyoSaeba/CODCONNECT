namespace CODConnect.Core;

public enum LinkState
{
    Down,
    Up,
}

public readonly record struct CapturedFrame(ReadOnlyMemory<byte> Buffer, DateTimeOffset Timestamp)
{
    public int Length => Buffer.Length;
}
