namespace CODConnect.Protocol;

public sealed record EndpointInfo(
    IReadOnlyList<string> Addresses,
    int Port,
    string? Hub = null,
    string? Username = null,
    string? Password = null);

public sealed record RoomMember(string MemberId, string DisplayName, DateTimeOffset JoinedUtc, EndpointInfo? Endpoint = null);

public sealed record RoomInfo(string RoomCode, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc, IReadOnlyList<RoomMember> Members)
{
    public int MemberCount => Members.Count;
}

public sealed record HeartbeatRequest(string Secret);

public enum HeartbeatResult
{
    Alive,

    RoomGone,

    MemberGone,

    RoomClosed,
}

public enum RestoreResult
{
    Restored,

    Taken,

    Closed,
}

public sealed record RestoreRoomRequest(string AdminKey, string MemberId, string MemberSecret, string DisplayName, EndpointInfo Endpoint);

public sealed record RejoinRequest(string Secret, string DisplayName, EndpointInfo? Endpoint = null);

public sealed record UpdateEndpointRequest(string Secret, EndpointInfo Endpoint);

public sealed record CreateRoomRequest(string DisplayName, EndpointInfo Endpoint);

public sealed record CreateRoomResponse(string RoomCode, string AdminKey, string MemberId, string MemberSecret, DateTimeOffset ExpiresUtc);

public sealed record JoinRequest(string DisplayName, EndpointInfo? Endpoint = null);

public sealed record JoinResponse(string MemberId, string RoomCode, string MemberSecret, DateTimeOffset ExpiresUtc, IReadOnlyList<RoomMember> Peers);
