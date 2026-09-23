using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using CODConnect.Core;
using CODConnect.Core.Diagnostics;
using CODConnect.PacketEngine.Native;

namespace CODConnect.PacketEngine;

public sealed class NpcapConsoleInterface : IConsoleNetworkInterface
{
    private const int SnapLength = 65535;
    private const int ReadTimeoutMs = 100;
    private const int QueueCapacity = 4096;

    private readonly IntPtr _handle;
    private readonly BlockingCollection<CapturedFrame> _queue = new(QueueCapacity);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread _captureThread;
    private readonly object _ioLock = new();
    private readonly NetworkCounters? _counters;
    private readonly MacAddress _mac;
    private readonly int _mtu;
    private readonly string _friendlyName;
    private readonly EchoFilter _echo = new();
    private volatile bool _disposed;

    public NpcapConsoleInterface(PcapAdapterInfo adapter, NetworkCounters? counters = null, string? bpfFilter = null)
    {
        Adapter = adapter;
        _counters = counters;
        _friendlyName = adapter.FriendlyName;
        _mac = adapter.Mac;
        _mtu = 1500;

        var errbuf = new StringBuilder(256);
        _handle = PcapNative.pcap_open_live(adapter.PcapName, SnapLength, promisc: 1, ReadTimeoutMs, errbuf);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Failed to open adapter '{adapter.FriendlyName}': {errbuf}. "
                + "If Npcap was installed with 'Admin Only', run elevated.");
        }

        ReceiveOnly = PcapNative.pcap_setdirection(_handle, PcapNative.PcapDirectionIn) == 0;

        if (bpfFilter is not null)
        {
            ApplyFilter(bpfFilter);
        }

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = $"codconnect-capture-{adapter.FriendlyName}",
        };
        _captureThread.Start();
    }

    public PcapAdapterInfo Adapter { get; }

    public bool ReceiveOnly { get; }

    public string Name => _friendlyName;

    public MacAddress GetMac() => _mac;

    public int GetMtu() => _mtu;

    private static readonly TimeSpan LinkStateCacheTtl = TimeSpan.FromSeconds(1);
    private readonly object _linkLock = new();
    private LinkState _cachedLink = LinkState.Up;
    private long? _cachedLinkAt;

    public static bool IsFresh(long now, long? takenAt, TimeSpan ttl)
        => takenAt is { } at && now - at < ttl.TotalMilliseconds;

    public LinkState GetLinkState()
    {
        if (_disposed)
        {
            return LinkState.Down;
        }

        var now = Environment.TickCount64;
        lock (_linkLock)
        {
            if (IsFresh(now, _cachedLinkAt, LinkStateCacheTtl))
            {
                return _cachedLink;
            }
        }

        var state = LinkState.Up;
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (Adapter.PcapName.Contains(nic.Id, StringComparison.OrdinalIgnoreCase))
                {
                    state = nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                        ? LinkState.Up
                        : LinkState.Down;
                    break;
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
        }

        lock (_linkLock)
        {
            _cachedLink = state;
            _cachedLinkAt = now;
        }

        return state;
    }

    public CapturedFrame? CaptureFrame(CancellationToken cancellationToken = default)
    {
        if (_queue.TryTake(out var frame, 0, cancellationToken))
        {
            return frame;
        }

        return null;
    }

    public ValueTask InjectFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NpcapConsoleInterface));
        }

        byte[] owned;
        if (MemoryMarshal.TryGetArray(frame, out var segment)
            && segment.Offset == 0
            && segment.Count == segment.Array!.Length)
        {
            owned = segment.Array;
        }
        else
        {
            owned = frame.ToArray();
        }

        lock (_ioLock)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NpcapConsoleInterface));
            }

            _echo.Remember(owned);
            if (PcapNative.pcap_sendpacket(_handle, owned, owned.Length) != 0)
            {
                _counters?.RecordDropped();
                throw new InvalidOperationException(
                    $"pcap_sendpacket failed on '{_friendlyName}': {PcapNative.pcap_geterr(_handle)}");
            }
        }

        _counters?.RecordInjected(owned.Length, broadcast: IsBroadcastOrMulticast(owned));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        await _captureThread.JoinAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        lock (_ioLock)
        {
            PcapNative.pcap_close(_handle);
        }

        _queue.Dispose();
        _cancellation.Dispose();
    }

    private void ApplyFilter(string filter)
    {
        if (PcapNative.pcap_compile(_handle, out var program, filter, optimize: 1, PcapNative.PcapNetmaskUnknown) != 0)
        {
            throw new InvalidOperationException(
                $"pcap_compile failed for '{filter}': {PcapNative.pcap_geterr(_handle)}");
        }

        try
        {
            if (PcapNative.pcap_setfilter(_handle, ref program) != 0)
            {
                throw new InvalidOperationException(
                    $"pcap_setfilter failed for '{filter}': {PcapNative.pcap_geterr(_handle)}");
            }
        }
        finally
        {
            PcapNative.pcap_freecode(ref program);
        }
    }

    private void CaptureLoop()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            int result;
            IntPtr header;
            IntPtr data;
            lock (_ioLock)
            {
                if (_disposed)
                {
                    return;
                }

                result = PcapNative.pcap_next_ex(_handle, out header, out data);
            }

            if (result == 0)
            {
                continue;
            }

            if (result < 0)
            {
                return;
            }

            var pktHeader = Marshal.PtrToStructure<pcap_pkthdr>(header);
            var bytes = new byte[pktHeader.CapLen];
            Marshal.Copy(data, bytes, 0, (int)pktHeader.CapLen);
            if (_echo.IsEcho(bytes))
            {
                continue;
            }

            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(
                (long)pktHeader.TsSec * 1000 + pktHeader.TsUsec / 1000);
            var frame = new CapturedFrame(bytes, timestamp);

            if (!_queue.TryAdd(frame))
            {
                _queue.TryTake(out _);
                _queue.TryAdd(frame);
                _counters?.RecordDropped();
            }

            _counters?.RecordCaptured(bytes.Length, IsBroadcastOrMulticast(bytes));
        }
    }

    private static bool IsBroadcastOrMulticast(byte[] frame)
        => frame.Length >= 1 && (frame[0] & 0x01) != 0;
}

public sealed class EchoFilter
{
    private const int Capacity = 1024;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private readonly Queue<(ulong Hash, long Ticks)> _recent = new();
    private readonly Dictionary<ulong, int> _counts = new();

    public void Remember(ReadOnlySpan<byte> frame)
    {
        var hash = Hash(frame);
        lock (_lock)
        {
            Expire(Environment.TickCount64);
            if (_recent.Count == Capacity)
            {
                Forget(_recent.Dequeue().Hash);
            }

            _recent.Enqueue((hash, Environment.TickCount64));
            _counts[hash] = _counts.TryGetValue(hash, out var n) ? n + 1 : 1;
        }
    }

    public bool IsEcho(ReadOnlySpan<byte> frame)
    {
        var hash = Hash(frame);
        lock (_lock)
        {
            Expire(Environment.TickCount64);
            if (!_counts.ContainsKey(hash))
            {
                return false;
            }

            Forget(hash);
            return true;
        }
    }

    private void Expire(long now)
    {
        while (_recent.Count > 0 && now - _recent.Peek().Ticks > Window.TotalMilliseconds)
        {
            Forget(_recent.Dequeue().Hash);
        }
    }

    private void Forget(ulong hash)
    {
        if (_counts.TryGetValue(hash, out var n))
        {
            if (n <= 1) _counts.Remove(hash); else _counts[hash] = n - 1;
        }
    }

    private static ulong Hash(ReadOnlySpan<byte> data)
    {
        var length = data.Length;
        while (length > 14 && data[length - 1] == 0)
        {
            length--;
        }

        var hash = 14695981039346656037UL;
        foreach (var b in data[..length])
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash ^ (ulong)length;
    }
}

internal static class ThreadJoinExtensions
{
    public static async ValueTask JoinAsync(this Thread thread, TimeSpan timeout)
    {
        var completed = await Task.Run(() => thread.Join(timeout)).ConfigureAwait(false);
        if (!completed)
        {
            thread.Interrupt();
        }
    }
}
