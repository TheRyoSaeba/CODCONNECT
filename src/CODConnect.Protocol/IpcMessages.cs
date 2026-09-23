namespace CODConnect.Protocol;

public sealed record IpcRequest(
    string Op,
    string? DisplayName = null,
    string? RoomCode = null,
    string? Adapter = null,
    bool AllowInternetAdapter = false,
    string? Message = null,
    string? Mode = null,
    IpcDevSettings? DevSettings = null);

public sealed record IpcDevSettings(bool FriendsConsoleAccess, bool RelayOnly, string RoomServer, string DefaultRoomServer);

public sealed record IpcWifiNetwork(string Ssid, string Passphrase, string Band);

public sealed record IpcWifiCapability(bool Supported, string Reason, string? Adapter, string? Band, int? Channel, IReadOnlyList<string> Warnings);

public sealed record IpcAdapterInfo(
    int Index,
    string Name,
    string Mac,
    IReadOnlyList<string> Addresses,
    string LinkState,
    bool IsLoopback,
    bool CarriesInternet = false,
    string Kind = "Other",
    bool Recommended = false);

public sealed record IpcLease(string Mac, string Ip, string? Hostname);

public sealed record IpcDevice(string Mac, string Side, string? Ip, string? Hostname, string? Identity, DateTimeOffset LastSeenUtc);

public sealed record IpcStatus(
    bool HasSession,
    string? Role,
    string? RoomCode,
    string? TunnelState,
    NetworkCounterSnapshotDto? Counters,
    IReadOnlyList<IpcLease> Leases,
    IReadOnlyList<IpcDevice>? Devices = null,
    bool LocalConsoleReady = false,
    bool RemoteConsoleReady = false,
    string? ConnectionKind = null,
    string? Transport = null,
    string? LastEndReason = null,
    RoomChatSnapshot? Chat = null,
    IpcWifiNetwork? WifiNetwork = null,
    string? ConsoleInternet = null,
    IReadOnlyList<IpcPlayer>? Players = null);

public sealed record IpcPlayer(string Name, bool Connected, string? Console, bool ConsoleReady, bool Relay);

public sealed record NetworkCounterSnapshotDto(
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

public sealed record IpcResponse(
    bool Ok,
    string? Error = null,
    string? RoomCode = null,
    IpcStatus? Status = null,
    IReadOnlyList<IpcAdapterInfo>? Adapters = null,
    IpcWifiCapability? WifiCapability = null,
    IpcDevSettings? DevSettings = null);
