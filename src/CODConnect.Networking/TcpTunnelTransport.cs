using System.Net;
using System.Net.Sockets;
using CODConnect.Core.Diagnostics;

namespace CODConnect.Networking;

public sealed record TcpTunnelOptions
{
    public int? ListenPort { get; init; }

    public string? ConnectHost { get; init; }

    public int? ConnectPort { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed class TcpTunnelTransport : ITunnelTransport
{
    private readonly TcpTunnelOptions _options;
    private readonly NetworkCounters? _counters;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stopCts = new();
    private readonly object _gate = new();

    private int _state;
    private int _stopRequested;
    private int _startGate;
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _readLoopTask;

    public TcpTunnelTransport(TcpTunnelOptions options, NetworkCounters? counters = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _counters = counters;
    }

    public TunnelState State => (TunnelState)Volatile.Read(ref _state);

    public int? BoundPort { get; private set; }

    public event Action<CapturedFrame>? FrameReceived;

    public event Action<TunnelState>? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ValidateOptions();
        if (Interlocked.Exchange(ref _startGate, 1) != 0)
        {
            throw new InvalidOperationException("This transport has already been started; v1 transports are single-use.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_options.ListenPort is int listenPort)
        {
            StartListen(listenPort);
        }
        else
        {
            await StartConnectAsync(_options.ConnectHost!, _options.ConnectPort!.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        if (State != TunnelState.Connected)
        {
            throw new InvalidOperationException($"Frames can only be sent while the transport is Connected (current state: {State}).");
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NetworkStream? stream = _stream;
            if (State != TunnelState.Connected || stream is null)
            {
                throw new InvalidOperationException($"Frames can only be sent while the transport is Connected (current state: {State}).");
            }

            FrameFraming.WriteFrame(stream, frame.Span, cancellationToken);
            _counters?.RecordInjected(frame.Length, broadcast: false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _counters?.RecordDropped();
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            return;
        }

        try
        {
            _stopCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        TcpClient? client = Interlocked.Exchange(ref _client, null);
        NetworkStream? stream = Interlocked.Exchange(ref _stream, null);
        TcpListener? listener = Interlocked.Exchange(ref _listener, null);
        _readLoopTask = null;

        if (client is not null)
        {
            AbortivelyClose(client);
        }

        if (stream is not null)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (client is not null)
        {
            try
            {
                client.Dispose();
            }
            catch
            {
            }
        }

        bool changed;
        lock (_gate)
        {
            if (_state != (int)TunnelState.Failed && _state != (int)TunnelState.Disconnected)
            {
                _state = (int)TunnelState.Disconnected;
                changed = true;
            }
            else
            {
                changed = false;
            }
        }

        if (changed)
        {
            StateChanged?.Invoke(TunnelState.Disconnected);
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private void ValidateOptions()
    {
        bool hasListen = _options.ListenPort is not null;
        bool hasHost = _options.ConnectHost is not null;
        bool hasConnectPort = _options.ConnectPort is not null;

        if (_options.ListenPort is int listenPort && (listenPort < 0 || listenPort > 65535))
        {
            throw new ArgumentOutOfRangeException(nameof(TcpTunnelOptions.ListenPort), listenPort, "ListenPort must be between 0 and 65535.");
        }

        if (_options.ConnectPort is int connectPort && (connectPort < 1 || connectPort > 65535))
        {
            throw new ArgumentOutOfRangeException(nameof(TcpTunnelOptions.ConnectPort), connectPort, "ConnectPort must be between 1 and 65535.");
        }

        if (hasHost != hasConnectPort)
        {
            throw new ArgumentException("ConnectHost and ConnectPort must be configured together.", nameof(TcpTunnelOptions));
        }

        if (hasListen == (hasHost && hasConnectPort))
        {
            throw new ArgumentException(
                "Exactly one of ListenPort (listen mode) or ConnectHost/ConnectPort (connect mode) must be configured.",
                nameof(TcpTunnelOptions));
        }
    }

    private void StartListen(int port)
    {
        TransitionTo(TunnelState.Connecting);
        var listener = new TcpListener(IPAddress.IPv6Any, port);
        try
        {
            listener.Server.DualMode = true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }

        try
        {
            try
            {
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }
            catch (SocketException)
            {
            }

            listener.Start();
            BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            _listener = listener;
            _ = RunAcceptLoopAsync(listener);
        }
        catch
        {
            CloseQuietly(listener);
            TransitionTo(TunnelState.Failed);
            throw;
        }
    }

    private async Task StartConnectAsync(string host, int port, CancellationToken externalToken)
    {
        TransitionTo(TunnelState.Connecting);
        var client = new TcpClient();
        _client = client;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _stopCts.Token);
        timeoutCts.CancelAfter(_options.ConnectTimeout);
        try
        {
            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!externalToken.IsCancellationRequested && !_stopCts.IsCancellationRequested)
        {
            CloseQuietly(client);
            _client = null;
            TransitionTo(TunnelState.Failed);
            throw new TimeoutException($"Timed out connecting to {host}:{port} within {_options.ConnectTimeout}.");
        }
        catch
        {
            CloseQuietly(client);
            _client = null;
            TransitionTo(TunnelState.Failed);
            throw;
        }

        EstablishTunnel(client);
    }

    private async Task RunAcceptLoopAsync(TcpListener listener)
    {
        try
        {
            TcpClient peer = await listener.AcceptTcpClientAsync(_stopCts.Token).ConfigureAwait(false);

            for (int drained = 0; drained < 8 && listener.Pending(); drained++)
            {
                TcpClient extra = await listener.AcceptTcpClientAsync(CancellationToken.None).ConfigureAwait(false);
                CloseQuietly(extra);
            }

            listener.Stop();
            EstablishTunnel(peer);
        }
        catch (OperationCanceledException)
        {
            CloseQuietly(listener);
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
            TransitionTo(TunnelState.Failed);
        }
    }

    private void EstablishTunnel(TcpClient peer)
    {
        if (Volatile.Read(ref _stopRequested) != 0)
        {
            CloseQuietly(peer);
            return;
        }

        peer.NoDelay = true;
        _client = peer;
        NetworkStream stream = peer.GetStream();
        _stream = stream;
        _readLoopTask = ReadLoopAsync();
        TransitionTo(TunnelState.Connected);
    }

    private async Task ReadLoopAsync()
    {
        NetworkStream? stream = _stream;
        if (stream is null)
        {
            return;
        }

        var reader = new FrameReader(stream);
        try
        {
            while (true)
            {
                byte[]? frame = await reader.ReadFrameAsync(_stopCts.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    TransitionTo(TunnelState.Disconnected);
                    return;
                }

                _counters?.RecordCaptured(frame.Length, broadcast: false);
                FrameReceived?.Invoke(new CapturedFrame(frame, DateTimeOffset.UtcNow));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            TransitionTo(TunnelState.Failed);
        }
        catch (InvalidDataException)
        {
            _counters?.RecordMalformed();
            TransitionTo(TunnelState.Failed);
        }
        catch
        {
            TransitionTo(TunnelState.Failed);
        }
    }

    private void TransitionTo(TunnelState newState)
    {
        bool changed;
        lock (_gate)
        {
            if (Volatile.Read(ref _stopRequested) != 0 || _state == (int)newState)
            {
                return;
            }

            _state = (int)newState;
            changed = true;
        }

        if (changed)
        {
            StateChanged?.Invoke(newState);
        }
    }

    private static void AbortivelyClose(TcpClient client)
    {
        try
        {
            client.Client.LingerState = new LingerOption(true, 0);
            client.Client.Dispose();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    private static void CloseQuietly(TcpClient client)
    {
        if (client is null)
        {
            return;
        }

        AbortivelyClose(client);

        try
        {
            client.Dispose();
        }
        catch
        {
        }
    }

    private static void CloseQuietly(TcpListener listener)
    {
        try
        {
            listener.Stop();
        }
        catch
        {
        }
    }
}
