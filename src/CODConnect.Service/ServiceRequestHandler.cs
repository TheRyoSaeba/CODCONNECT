namespace CODConnect.Service;

public interface IIpcHandler
{
    Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken = default);
}

public sealed class ServiceRequestHandler : IIpcHandler
{
    private readonly SessionManager _sessions;
    private readonly Func<IReadOnlyList<IpcAdapterInfo>> _listAdapters;

    public ServiceRequestHandler(SessionManager sessions, Func<IReadOnlyList<IpcAdapterInfo>> listAdapters)
    {
        _sessions = sessions;
        _listAdapters = listAdapters;
    }

    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return request.Op switch
            {
                "ping" => new IpcResponse(true),
                "list-adapters" => new IpcResponse(true, Adapters: _listAdapters()),
                "host" => await HostAsync(request, cancellationToken).ConfigureAwait(false),
                "join" => await JoinAsync(request, cancellationToken).ConfigureAwait(false),
                "status" => new IpcResponse(true, Status: BuildStatus()),
                "chat-send" => SendChat(request),
                "wifi-capability" => new IpcResponse(true, WifiCapability: await _sessions.CheckWifiAsync(cancellationToken).ConfigureAwait(false)),
                "stop" => await StopAsync().ConfigureAwait(false),
                "dev-settings" => new IpcResponse(true, DevSettings: DevSnapshot()),
                "set-dev-settings" => await SetDevSettingsAsync(request, cancellationToken).ConfigureAwait(false),
                _ => new IpcResponse(false, Error: $"unknown op '{request.Op}'"),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new IpcResponse(false, Error: ex.Message);
        }
    }

    private async Task<IpcResponse> HostAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var roomCode = await _sessions.StartHostAsync(request.DisplayName, request.Adapter, request.AllowInternetAdapter, IsWifi(request), cancellationToken).ConfigureAwait(false);
        return new IpcResponse(true, RoomCode: roomCode);
    }

    private async Task<IpcResponse> JoinAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        await _sessions.StartJoinAsync(request.RoomCode ?? string.Empty, request.DisplayName, request.Adapter, request.AllowInternetAdapter, IsWifi(request), cancellationToken).ConfigureAwait(false);
        return new IpcResponse(true);
    }

    private async Task<IpcResponse> StopAsync()
    {
        await _sessions.StopAsync().ConfigureAwait(false);
        return new IpcResponse(true);
    }

    private IpcDevSettings DevSnapshot()
    {
        var dev = _sessions.Dev ?? throw new InvalidOperationException("Developer options need the CODCONNECT service.");
        return new IpcDevSettings(dev.FriendsConsoleAccess, dev.RelayOnly, dev.RoomServer, dev.DefaultRoomServer);
    }

    private async Task<IpcResponse> SetDevSettingsAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var dev = _sessions.Dev ?? throw new InvalidOperationException("Developer options need the CODCONNECT service.");
        var wanted = request.DevSettings ?? throw new ArgumentException("No settings given.");
        if (!string.Equals(wanted.RoomServer.TrimEnd('/'), dev.RoomServer.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            if (_sessions.HasSession)
            {
                throw new InvalidOperationException("Leave the room before changing the room server.");
            }

            await dev.SetRoomServerAsync(wanted.RoomServer, cancellationToken).ConfigureAwait(false);
        }

        dev.SetToggles(wanted.FriendsConsoleAccess, wanted.RelayOnly);
        return new IpcResponse(true, DevSettings: DevSnapshot());
    }

    private static bool IsWifi(IpcRequest request) => string.Equals(request.Mode, "wifi", StringComparison.OrdinalIgnoreCase);

    private IpcResponse SendChat(IpcRequest request)
    {
        _sessions.SendChat(request.Message ?? string.Empty);
        return new IpcResponse(true, Status: BuildStatus());
    }

    private IpcStatus BuildStatus()
    {
        var counters = _sessions.Counters;
        return new IpcStatus(
            HasSession: _sessions.HasSession,
            Role: _sessions.Role,
            RoomCode: _sessions.RoomCode,
            TunnelState: _sessions.TunnelState,
            Counters: counters is null
                ? null
                : new NetworkCounterSnapshotDto(
                    counters.Value.FramesCaptured, counters.Value.FramesInjected,
                    counters.Value.BroadcastFramesIn, counters.Value.BroadcastFramesOut,
                    counters.Value.UnicastFramesIn, counters.Value.UnicastFramesOut,
                    counters.Value.FramesDropped, counters.Value.MalformedFrames,
                    counters.Value.BytesIn, counters.Value.BytesOut),
            Leases: _sessions.Leases,
            Devices: _sessions.Devices,
            LocalConsoleReady: _sessions.LocalConsoleReady,
            RemoteConsoleReady: _sessions.RemoteConsoleReady,
            ConnectionKind: _sessions.ConnectionKind,
            Transport: _sessions.ActiveTransport,
            LastEndReason: _sessions.LastEndReason,
            Chat: _sessions.Chat,
            WifiNetwork: _sessions.WifiNetwork,
            ConsoleInternet: _sessions.ConsoleInternet,
            Players: _sessions.Players,
            Progress: _sessions.Progress);
    }
}
