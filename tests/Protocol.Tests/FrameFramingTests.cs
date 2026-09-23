using System.Buffers.Binary;

namespace CODConnect.Protocol.Tests;

public class FrameFramingTests
{
    [Fact]
    public void WriteFrame_ThenTryReadFrame_RoundtripsViaMemoryStream()
    {
        byte[] payload = MakePayload(1514, salt: 7);
        var stream = new MemoryStream();

        FrameFraming.WriteFrame(stream, payload);

        byte[] wire = stream.ToArray();
        Assert.Equal(payload.Length + FrameFraming.LengthPrefixBytes, wire.Length);
        Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32LittleEndian(wire));

        bool ok = FrameFraming.TryReadFrame(wire, out int consumed, out byte[] frame);
        Assert.True(ok);
        Assert.Equal(wire.Length, consumed);
        Assert.Equal(payload, frame);
    }

    [Fact]
    public void TryReadFrame_EmptyBuffer_ReturnsFalseWithZeroConsumed()
    {
        bool ok = FrameFraming.TryReadFrame(ReadOnlySpan<byte>.Empty, out int consumed, out byte[] frame);

        Assert.False(ok);
        Assert.Equal(0, consumed);
        Assert.Empty(frame);
    }

    [Fact]
    public void TryReadFrame_PartialPrefixOrPartialPayload_ReturnsFalseWithZeroConsumed()
    {
        byte[] payload = MakePayload(64, salt: 1);
        byte[] wire = ToWire(payload);

        for (int take = 1; take < FrameFraming.LengthPrefixBytes; take++)
        {
            bool ok = FrameFraming.TryReadFrame(wire.AsSpan(0, take), out int consumed, out _);
            Assert.False(ok);
            Assert.Equal(0, consumed);
        }

        for (int take = FrameFraming.LengthPrefixBytes; take < wire.Length; take++)
        {
            bool ok = FrameFraming.TryReadFrame(wire.AsSpan(0, take), out int consumed, out _);
            Assert.False(ok);
            Assert.Equal(0, consumed);
        }
    }

    [Fact]
    public void TryReadFrame_TwoSequentialFrames_ParsesBothWithCorrectConsumedTotals()
    {
        byte[] first = MakePayload(61, salt: 11);
        byte[] second = MakePayload(1514, salt: 22);
        byte[] wire = ToWire(first, second);
        var buffer = wire.AsSpan();

        bool ok1 = FrameFraming.TryReadFrame(buffer, out int consumed1, out byte[] frame1);
        Assert.True(ok1);
        Assert.Equal(first, frame1);
        Assert.Equal(FrameFraming.LengthPrefixBytes + first.Length, consumed1);

        bool ok2 = FrameFraming.TryReadFrame(buffer.Slice(consumed1), out int consumed2, out byte[] frame2);
        Assert.True(ok2);
        Assert.Equal(second, frame2);
        Assert.Equal(FrameFraming.LengthPrefixBytes + second.Length, consumed2);

        Assert.Equal(wire.Length, consumed1 + consumed2);
    }

    [Fact]
    public void WriteFrame_ZeroLengthFrame_ThrowsArgumentException()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentException>(() => FrameFraming.WriteFrame(stream, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void WriteFrame_OversizeFrame_ThrowsArgumentException()
    {
        using var stream = new MemoryStream();
        byte[] oversize = new byte[TunnelLimits.MaxFrameBytes + 1];
        Assert.Throws<ArgumentException>(() => FrameFraming.WriteFrame(stream, oversize));
    }

    [Fact]
    public void TryReadFrame_DeclaredZeroLength_ThrowsInvalidDataException()
    {
        Assert.Throws<InvalidDataException>(() =>
            FrameFraming.TryReadFrame(PrefixBuffer(0), out _, out _));
    }

    [Fact]
    public void TryReadFrame_DeclaredLengthAboveMaxFrameBytes_ThrowsInvalidDataException()
    {
        Assert.Throws<InvalidDataException>(() =>
            FrameFraming.TryReadFrame(PrefixBuffer((uint)TunnelLimits.MaxFrameBytes + 1), out _, out _));
    }

    [Fact]
    public void WriteFrame_AcceptsBoundaryLengths()
    {
        using var stream = new MemoryStream();
        FrameFraming.WriteFrame(stream, new byte[1]);
        FrameFraming.WriteFrame(stream, new byte[TunnelLimits.MaxFrameBytes]);
        Assert.Equal(1 + TunnelLimits.MaxFrameBytes + 2 * FrameFraming.LengthPrefixBytes, stream.Length);
    }

    private static byte[] PrefixBuffer(uint declaredLength)
    {
        Span<byte> prefix = stackalloc byte[FrameFraming.LengthPrefixBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, declaredLength);
        return prefix.ToArray();
    }

    private static byte[] ToWire(params byte[][] payloads)
    {
        var stream = new MemoryStream();
        foreach (byte[] payload in payloads)
        {
            FrameFraming.WriteFrame(stream, payload);
        }

        return stream.ToArray();
    }

    private static byte[] MakePayload(int length, int salt)
    {
        var payload = new byte[length];
        for (int i = 0; i < length; i++)
        {
            payload[i] = (byte)((i * 31 + salt * 17) & 0xFF);
        }

        return payload;
    }
}

public class FrameReaderTests
{
    [Fact]
    public async Task ReadFrameAsync_OneByteChunks_ReassemblesFramesCorrectly()
    {
        byte[] first = MakePayload(77, salt: 3);
        byte[] second = MakePayload(1514, salt: 4);
        var source = new MemoryStream();
        FrameFraming.WriteFrame(source, first);
        FrameFraming.WriteFrame(source, second);
        source.Position = 0;

        using var chunked = new OneByteReadStream(source);
        var reader = new FrameReader(chunked);

        byte[]? read1 = await reader.ReadFrameAsync();
        byte[]? read2 = await reader.ReadFrameAsync();
        byte[]? end = await reader.ReadFrameAsync();

        Assert.Equal(first, read1);
        Assert.Equal(second, read2);
        Assert.Null(end);
    }

    [Fact]
    public async Task ReadFrameAsync_EmptyStream_ReturnsNullOnCleanEof()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());
        var reader = new FrameReader(stream);

        Assert.Null(await reader.ReadFrameAsync());
    }

    [Fact]
    public async Task ReadFrameAsync_DeclaredOversizeLength_ThrowsInvalidDataException()
    {
        using var stream = new MemoryStream();
        stream.Write(PrefixBuffer((uint)TunnelLimits.MaxFrameBytes + 1));
        stream.Position = 0;
        var reader = new FrameReader(stream);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadFrameAsync());
    }

    [Fact]
    public async Task ReadFrameAsync_DeclaredZeroLength_ThrowsInvalidDataException()
    {
        using var stream = new MemoryStream();
        stream.Write(PrefixBuffer(0));
        stream.Position = 0;
        var reader = new FrameReader(stream);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadFrameAsync());
    }

    private static byte[] PrefixBuffer(uint declaredLength)
    {
        Span<byte> prefix = stackalloc byte[FrameFraming.LengthPrefixBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, declaredLength);
        return prefix.ToArray();
    }

    private static byte[] MakePayload(int length, int salt)
    {
        var payload = new byte[length];
        for (int i = 0; i < length; i++)
        {
            payload[i] = (byte)((i * 31 + salt * 17) & 0xFF);
        }

        return payload;
    }

    private sealed class OneByteReadStream : Stream
    {
        private readonly MemoryStream _inner;

        public OneByteReadStream(MemoryStream inner) => _inner = inner;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0)
            {
                return 0;
            }

            return _inner.Read(buffer, offset, 1);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
