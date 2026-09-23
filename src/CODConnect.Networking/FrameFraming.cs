using System.Buffers.Binary;

namespace CODConnect.Networking;

public static class FrameFraming
{
    public const int LengthPrefixBytes = 4;

    public static void WriteFrame(Stream stream, ReadOnlySpan<byte> frame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.Length < 1 || frame.Length > TunnelLimits.MaxFrameBytes)
        {
            throw new ArgumentException(
                $"Frame length must be between 1 and {TunnelLimits.MaxFrameBytes} bytes, but was {frame.Length}.",
                nameof(frame));
        }

        Span<byte> prefix = stackalloc byte[LengthPrefixBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)frame.Length);
        stream.Write(prefix);
        stream.Write(frame);
    }

    public static bool TryReadFrame(ReadOnlySpan<byte> buffer, out int consumed, out byte[] frame)
    {
        if (buffer.Length < LengthPrefixBytes)
        {
            consumed = 0;
            frame = Array.Empty<byte>();
            return false;
        }

        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (declared < 1 || declared > TunnelLimits.MaxFrameBytes)
        {
            throw new InvalidDataException(
                $"Declared frame length {declared} is outside the allowed range 1..{TunnelLimits.MaxFrameBytes}.");
        }

        int total = LengthPrefixBytes + (int)declared;
        if (buffer.Length < total)
        {
            consumed = 0;
            frame = Array.Empty<byte>();
            return false;
        }

        consumed = total;
        frame = buffer.Slice(LengthPrefixBytes, (int)declared).ToArray();
        return true;
    }
}

public sealed class FrameReader
{
    private readonly Stream _stream;
    private readonly byte[] _prefix = new byte[FrameFraming.LengthPrefixBytes];

    public FrameReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public async Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        int prefixBytes = await ReadExactlyAsync(_prefix, cancellationToken).ConfigureAwait(false);
        if (prefixBytes == 0)
        {
            return null;
        }

        if (prefixBytes < FrameFraming.LengthPrefixBytes)
        {
            throw new InvalidDataException("Stream ended in the middle of a frame length prefix.");
        }

        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(_prefix);
        if (declared < 1 || declared > TunnelLimits.MaxFrameBytes)
        {
            throw new InvalidDataException(
                $"Declared frame length {declared} is outside the allowed range 1..{TunnelLimits.MaxFrameBytes}.");
        }

        var frame = new byte[declared];
        int frameBytes = await ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
        if (frameBytes < frame.Length)
        {
            throw new InvalidDataException("Stream ended in the middle of a frame payload.");
        }

        return frame;
    }

    private async Task<int> ReadExactlyAsync(byte[] target, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < target.Length)
        {
            int read = await _stream.ReadAsync(target.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
