using System.Collections.Concurrent;

namespace CODConnect.Rendezvous;

public sealed class RateLimiterOptions
{
    public int RequestsPerWindow { get; init; } = 30;

    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);
}

public sealed class RateLimiter
{
    private const int EvictionCallInterval = 256;
    private const int EvictionSizeThreshold = 1024;

    private sealed class WindowState
    {
        public object Lock { get; } = new();
        public long WindowId { get; set; }
        public int Count { get; set; }
    }

    private readonly ConcurrentDictionary<string, WindowState> _windows = new(StringComparer.Ordinal);
    private readonly RateLimiterOptions _options;
    private readonly TimeProvider _timeProvider;
    private long _callCount;

    public RateLimiter(RateLimiterOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new RateLimiterOptions();
        if (_options.RequestsPerWindow < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RequestsPerWindow must be at least 1.");
        }

        if (_options.Window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Window must be positive.");
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool Allow(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            key = "unknown";
        }

        var now = _timeProvider.GetUtcNow();
        var windowId = now.UtcTicks / _options.Window.Ticks;
        var state = _windows.GetOrAdd(key, static _ => new WindowState());

        bool allowed;
        lock (state.Lock)
        {
            if (state.WindowId != windowId)
            {
                state.WindowId = windowId;
                state.Count = 0;
            }

            if (state.Count < _options.RequestsPerWindow)
            {
                state.Count++;
                allowed = true;
            }
            else
            {
                allowed = false;
            }
        }

        EvictStaleWindows(now);
        return allowed;
    }

    private void EvictStaleWindows(DateTimeOffset now)
    {
        var trigger = Interlocked.Increment(ref _callCount);
        if (_windows.IsEmpty
            || (trigger % EvictionCallInterval != 0 && _windows.Count < EvictionSizeThreshold))
        {
            return;
        }

        foreach (var (key, state) in _windows)
        {
            lock (state.Lock)
            {
                var windowEndTicks = (state.WindowId + 1) * _options.Window.Ticks;
                if (now.UtcTicks >= windowEndTicks)
                {
                    _windows.TryRemove(key, out _);
                }
            }
        }
    }
}
