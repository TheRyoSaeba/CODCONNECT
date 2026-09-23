using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using CODConnect.Core.Diagnostics;

namespace CODConnect.Protocol.Tests;

public class TcpTunnelTransportTests
{
    private const int WaitTimeoutMs = 5_000;

    [Fact]
    public async Task StartAsync_WithoutExactlyOneModeConfigured_ThrowsArgumentException()
    {
        await using var neither = new TcpTunnelTransport(new TcpTunnelOptions());
        await Assert.ThrowsAsync<ArgumentException>(() => neither.StartAsync());

        await using var both = new TcpTunnelTransport(new TcpTunnelOptions
        {
            ListenPort = 50000,
            ConnectHost = "127.0.0.1",
            ConnectPort = 50001,
        });
        await Assert.ThrowsAsync<ArgumentException>(() => both.StartAsync());

        await using var partial = new TcpTunnelTransport(new TcpTunnelOptions { ConnectHost = "127.0.0.1" });
        await Assert.ThrowsAsync<ArgumentException>(() => partial.StartAsync());

        Assert.Equal(TunnelState.Disconnected, neither.State);
        Assert.Equal(TunnelState.Disconnected, both.State);
        Assert.Equal(TunnelState.Disconnected, partial.State);
    }

    [Fact]
    public async Task StartAsync_ConnectRefused_TransportFails()
    {
        int deadPort = GetClosedLocalPort();
        await using var transport = new TcpTunnelTransport(new TcpTunnelOptions
        {
            ConnectHost = "127.0.0.1",
            ConnectPort = deadPort,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });

        await Assert.ThrowsAnyAsync<Exception>(() => transport.StartAsync());

        Assert.Equal(TunnelState.Failed, transport.State);
    }

    [Fact]
    public async Task SendFrameAsync_WhenDisconnected_ThrowsInvalidOperationException()
    {
        await using var transport = new TcpTunnelTransport(new TcpTunnelOptions { ListenPort = 0 });

        Assert.Equal(TunnelState.Disconnected, transport.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendFrameAsync(new byte[16]).AsTask());
    }

    [Fact]
    public async Task TwoTransports_ExchangeBidirectionalFrames_PreservingOrderAndBytes()
    {
        var listenerStates = new ConcurrentQueue<TunnelState>();
        var connectorStates = new ConcurrentQueue<TunnelState>();
        var listenerReceived = new ConcurrentQueue<byte[]>();
        var connectorReceived = new ConcurrentQueue<byte[]>();
        var listenerCounters = new NetworkCounters();
        var connectorCounters = new NetworkCounters();

        await using var listener = new TcpTunnelTransport(new TcpTunnelOptions { ListenPort = 0 }, listenerCounters);
        listener.StateChanged += listenerStates.Enqueue;
        listener.FrameReceived += frame => listenerReceived.Enqueue(frame.Buffer.ToArray());

        Assert.Equal(TunnelState.Disconnected, listener.State);
        Assert.Null(listener.BoundPort);

        await listener.StartAsync();
        Assert.Equal(TunnelState.Connecting, listener.State);
        int port = listener.BoundPort ?? throw new InvalidOperationException("BoundPort was not set in listen mode.");
        Assert.InRange(port, 1, 65535);

        await using var connector = new TcpTunnelTransport(new TcpTunnelOptions
        {
            ConnectHost = "127.0.0.1",
            ConnectPort = port,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        }, connectorCounters);
        connector.StateChanged += connectorStates.Enqueue;
        connector.FrameReceived += frame => connectorReceived.Enqueue(frame.Buffer.ToArray());

        await connector.StartAsync();
        Assert.Equal(TunnelState.Connected, connector.State);

        await WaitForAsync(() => listener.State == TunnelState.Connected, "listener side to reach Connected");

        Assert.Equal(new[] { TunnelState.Connecting, TunnelState.Connected }, listenerStates.ToArray());
        Assert.Equal(new[] { TunnelState.Connecting, TunnelState.Connected }, connectorStates.ToArray());

        var toListener = new List<byte[]>();
        var toConnector = new List<byte[]>();
        for (int i = 0; i < 12; i++)
        {
            int length = i switch
            {
                3 => 1514,
                7 => TunnelLimits.MaxFrameBytes,
                _ => 64 + (i * 13),
            };
            toListener.Add(MakePayload(length, salt: 100 + i));
            toConnector.Add(MakePayload(length, salt: 200 + i));
        }

        for (int i = 0; i < toListener.Count; i++)
        {
            await connector.SendFrameAsync(toListener[i]);
            await listener.SendFrameAsync(toConnector[i]);
        }

        await WaitForAsync(
            () => listenerReceived.Count >= toListener.Count && connectorReceived.Count >= toConnector.Count,
            "all frames to arrive on both sides");

        Assert.Equal(toListener.Count, listenerReceived.Count);
        Assert.Equal(toConnector.Count, connectorReceived.Count);

        byte[][] listenerSide = listenerReceived.ToArray();
        for (int i = 0; i < toListener.Count; i++)
        {
            Assert.Equal(toListener[i], listenerSide[i]);
        }

        byte[][] connectorSide = connectorReceived.ToArray();
        for (int i = 0; i < toConnector.Count; i++)
        {
            Assert.Equal(toConnector[i], connectorSide[i]);
        }

        Assert.Equal(1514, listenerSide[3].Length);
        Assert.Equal(TunnelLimits.MaxFrameBytes, listenerSide[7].Length);
        Assert.Equal(1514, connectorSide[3].Length);
        Assert.Equal(TunnelLimits.MaxFrameBytes, connectorSide[7].Length);

        Assert.Equal(toListener.Count, listenerCounters.FramesCaptured);
        Assert.Equal(toConnector.Count, listenerCounters.FramesInjected);
        Assert.Equal(toConnector.Count, connectorCounters.FramesCaptured);
        Assert.Equal(toListener.Count, connectorCounters.FramesInjected);
        Assert.Equal(0, listenerCounters.FramesDropped);
        Assert.Equal(0, connectorCounters.FramesDropped);
    }

    [Fact]
    public async Task StopAsync_OnOneSide_MovesOtherSideToFailed()
    {
        var connectorStates = new ConcurrentQueue<TunnelState>();
        TunnelPair pair = await EstablishTunnelAsync(listenerStates: null, connectorStates: connectorStates);
        try
        {
            await pair.Listener.StopAsync();
            Assert.Equal(TunnelState.Disconnected, pair.Listener.State);

            await WaitForAsync(
                () => pair.Connector.State == TunnelState.Failed,
                "connector side to reach Failed after the listener stopped");
            Assert.Contains(TunnelState.Failed, connectorStates.ToArray());
        }
        finally
        {
            await pair.Listener.DisposeAsync();
            await pair.Connector.DisposeAsync();
        }
    }

    [Fact]
    public async Task Listener_AcceptsExactlyOnePeer_SecondConnectionIsRefused()
    {
        TunnelPair pair = await EstablishTunnelAsync(listenerStates: null, connectorStates: null);
        try
        {
            using var extra = new TcpClient();
            await Assert.ThrowsAnyAsync<Exception>(
                () => extra.ConnectAsync(IPAddress.Parse("127.0.0.1"), pair.Port));
        }
        finally
        {
            await pair.Listener.DisposeAsync();
            await pair.Connector.DisposeAsync();
        }
    }

    private sealed record TunnelPair(TcpTunnelTransport Listener, TcpTunnelTransport Connector, int Port);

    private static async Task<TunnelPair> EstablishTunnelAsync(
        ConcurrentQueue<TunnelState>? listenerStates,
        ConcurrentQueue<TunnelState>? connectorStates)
    {
        var listener = new TcpTunnelTransport(new TcpTunnelOptions { ListenPort = 0 });
        if (listenerStates is not null)
        {
            listener.StateChanged += listenerStates.Enqueue;
        }

        await listener.StartAsync();
        int port = listener.BoundPort ?? throw new InvalidOperationException("BoundPort was not set in listen mode.");

        var connector = new TcpTunnelTransport(new TcpTunnelOptions
        {
            ConnectHost = "127.0.0.1",
            ConnectPort = port,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });
        if (connectorStates is not null)
        {
            connector.StateChanged += connectorStates.Enqueue;
        }

        try
        {
            await connector.StartAsync();
            Assert.Equal(TunnelState.Connected, connector.State);
            await WaitForAsync(() => listener.State == TunnelState.Connected, "listener side to reach Connected");
            return new TunnelPair(listener, connector, port);
        }
        catch
        {
            await connector.DisposeAsync();
            await listener.DisposeAsync();
            throw;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        long start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > WaitTimeoutMs)
            {
                throw new TimeoutException($"Timed out after {WaitTimeoutMs} ms waiting for {description}.");
            }

            await Task.Delay(10);
        }
    }

    private static int GetClosedLocalPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
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
