using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CODConnect.Protocol;

namespace CODConnect.Rendezvous;

public sealed record RoomStoreOptions
{
    public TimeSpan RoomTtl { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan ClosedMemory { get; init; } = TimeSpan.FromMinutes(10);

    public int MaxMembers { get; init; } = 6;

    public TimeSpan MemberTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

public enum HeartbeatStatus
{
    Alive,

    RoomGone,

    MemberGone,

    Forbidden,

    RoomClosed,
}

public enum JoinStatus
{
    Joined,
    UnknownRoom,
    RoomFull,
    InvalidName,
    NameTaken,
}

public sealed record JoinOutcome(JoinStatus Status, JoinResponse? Response, RoomInfo? RoomInfo);

public sealed class RoomStore
{
    private const int AdminKeyByteCount = 8;
    private const int MemberIdByteCount = 6;
    public const int MaxDisplayNameLength = 32;

    private static readonly byte[] DummyAdminKey = new byte[AdminKeyByteCount * 2];

    private readonly ConcurrentDictionary<string, RoomEntry> _rooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _closed = new(StringComparer.Ordinal);
    private readonly RoomStoreOptions _options;
    private readonly TimeProvider _timeProvider;

    public RoomStore(RoomStoreOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new RoomStoreOptions();
        if (_options.RoomTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RoomTtl must be positive.");
        }

        if (_options.MaxMembers < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxMembers must be at least 1.");
        }

        if (_options.MemberTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MemberTimeout must be positive.");
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public (string RoomCode, string AdminKey, string MemberId, string MemberSecret, DateTimeOffset ExpiresUtc) Create(string displayName, EndpointInfo endpoint)
    {
        ValidateName(displayName);
        ValidateEndpoint(endpoint);

        var now = _timeProvider.GetUtcNow();
        var expiresUtc = now + _options.RoomTtl;
        while (true)
        {
            var roomCode = RoomCode.Generate();
            var adminKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(AdminKeyByteCount));
            var memberId = Convert.ToHexString(RandomNumberGenerator.GetBytes(MemberIdByteCount)).ToLowerInvariant();
            var (memberSecret, memberSecretHash) = NewSecret();
            var entry = new RoomEntry(roomCode, Encoding.ASCII.GetBytes(adminKey), now, expiresUtc) { HostMemberId = memberId };
            entry.MemberSecrets[memberId] = memberSecretHash;
            entry.LastSeen[memberId] = now;
            entry.Members.Add(new RoomMember(memberId, displayName, now, endpoint));
            if (_rooms.TryAdd(roomCode, entry))
            {
                return (roomCode, adminKey, memberId, memberSecret, expiresUtc);
            }
        }
    }

    public RoomInfo? Get(string code)
    {
        if (!RoomCode.TryValidate(code, out var normalized) || !_rooms.TryGetValue(normalized, out var entry))
        {
            return null;
        }

        lock (entry.Lock)
        {
            return IsDeadLocked(entry) ? null : Snapshot(entry);
        }
    }

    public JoinOutcome Join(string code, string displayName, EndpointInfo? endpoint)
    {
        if (!RoomCode.TryValidate(code, out var normalized) || !_rooms.TryGetValue(normalized, out var entry))
        {
            return new JoinOutcome(JoinStatus.UnknownRoom, null, null);
        }

        lock (entry.Lock)
        {
            if (IsDeadLocked(entry))
            {
                return new JoinOutcome(JoinStatus.UnknownRoom, null, null);
            }

            if (entry.Members.Count >= _options.MaxMembers)
            {
                return new JoinOutcome(JoinStatus.RoomFull, null, null);
            }

            if (!IsValidName(displayName))
            {
                return new JoinOutcome(JoinStatus.InvalidName, null, null);
            }

            var key = NameKey(displayName);
            if (entry.Members.Any(m => NameKey(m.DisplayName) == key))
            {
                return new JoinOutcome(JoinStatus.NameTaken, null, null);
            }

            ValidateEndpoint(endpoint);

            var memberId = Convert.ToHexString(RandomNumberGenerator.GetBytes(MemberIdByteCount)).ToLowerInvariant();
            var (memberSecret, memberSecretHash) = NewSecret();
            var member = new RoomMember(memberId, displayName, _timeProvider.GetUtcNow(), endpoint);
            entry.Members.Add(member);
            entry.MemberSecrets[memberId] = memberSecretHash;
            entry.LastSeen[memberId] = member.JoinedUtc;

            var snapshot = Snapshot(entry);
            var response = new JoinResponse(memberId, entry.RoomCode, memberSecret, entry.ExpiresUtc, snapshot.Members);
            return new JoinOutcome(JoinStatus.Joined, response, snapshot);
        }
    }

    public bool UpdateEndpoint(string code, string memberId, string secret, EndpointInfo endpoint)
    {
        ValidateEndpoint(endpoint);
        if (!RoomCode.TryValidate(code, out var normalized) || !_rooms.TryGetValue(normalized, out var entry))
        {
            return false;
        }

        lock (entry.Lock)
        {
            if (IsDeadLocked(entry) || !entry.MemberSecrets.TryGetValue(memberId, out var expectedHash))
            {
                return false;
            }

            if (!CompareHash(expectedHash, secret))
            {
                return false;
            }

            var index = entry.Members.FindIndex(m => m.MemberId == memberId);
            if (index < 0)
            {
                return false;
            }

            var member = entry.Members[index];
            entry.Members[index] = member with { Endpoint = endpoint };
            entry.LastSeen[memberId] = _timeProvider.GetUtcNow();
            return true;
        }
    }

    public HeartbeatStatus Heartbeat(string code, string memberId, string secret)
    {
        if (!RoomCode.TryValidate(code, out var normalized) || !_rooms.TryGetValue(normalized, out var entry))
        {
            return WasClosed(normalized) ? HeartbeatStatus.RoomClosed : HeartbeatStatus.RoomGone;
        }

        lock (entry.Lock)
        {
            if (IsDeadLocked(entry))
            {
                return HeartbeatStatus.RoomGone;
            }

            if (!entry.MemberSecrets.TryGetValue(memberId, out var expectedHash))
            {
                return HeartbeatStatus.MemberGone;
            }

            if (!CompareHash(expectedHash, secret))
            {
                return HeartbeatStatus.Forbidden;
            }

            entry.LastSeen[memberId] = _timeProvider.GetUtcNow();
            return HeartbeatStatus.Alive;
        }
    }

    public bool Leave(string code, string memberId, string secret)
    {
        if (!RoomCode.TryValidate(code, out var normalized) || !_rooms.TryGetValue(normalized, out var entry))
        {
            return false;
        }

        lock (entry.Lock)
        {
            if (!entry.MemberSecrets.TryGetValue(memberId, out var expectedHash) || !CompareHash(expectedHash, secret))
            {
                return false;
            }

            if (memberId == entry.HostMemberId)
            {
                _rooms.TryRemove(normalized, out _);
                RememberClosed(normalized);
                return true;
            }

            RemoveMemberLocked(entry, memberId);
            return true;
        }
    }

    private static (string Plain, byte[] Hash) NewSecret()
    {
        var plain = Convert.ToHexString(RandomNumberGenerator.GetBytes(MemberIdByteCount)).ToLowerInvariant();
        return (plain, ComputeHash(plain));
    }

    private static byte[] ComputeHash(string secret) => System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(secret));

    private static bool CompareHash(byte[] expectedHash, string provided)
        => expectedHash.AsSpan().SequenceEqual(ComputeHash(provided ?? string.Empty));

    public bool Close(string code, string adminKey)
    {
        if (!RoomCode.TryValidate(code, out var normalized) || !_rooms.TryGetValue(normalized, out var entry))
        {
            _ = CompareKeys(DummyAdminKey, adminKey);
            return false;
        }

        if (!CompareKeys(entry.AdminKey, adminKey))
        {
            return false;
        }

        var removed = _rooms.TryRemove(normalized, out _);
        if (removed)
        {
            RememberClosed(normalized);
        }

        return removed;
    }

    public RestoreResult Restore(string code, RestoreRoomRequest request)
    {
        ValidateName(request.DisplayName);
        ValidateEndpoint(request.Endpoint);
        if (!RoomCode.TryValidate(code, out var normalized)
            || !IsHex(request.AdminKey, AdminKeyByteCount * 2)
            || !IsHex(request.MemberId, MemberIdByteCount * 2)
            || !IsHex(request.MemberSecret, MemberIdByteCount * 2))
        {
            throw new ArgumentException("Invalid restore request.");
        }

        if (WasClosed(normalized))
        {
            return RestoreResult.Closed;
        }

        var now = _timeProvider.GetUtcNow();
        var entry = new RoomEntry(normalized, Encoding.ASCII.GetBytes(request.AdminKey), now, now + _options.RoomTtl) { HostMemberId = request.MemberId };
        entry.MemberSecrets[request.MemberId] = ComputeHash(request.MemberSecret);
        entry.LastSeen[request.MemberId] = now;
        entry.Members.Add(new RoomMember(request.MemberId, request.DisplayName, now, request.Endpoint));
        if (_rooms.TryAdd(normalized, entry))
        {
            return RestoreResult.Restored;
        }

        return _rooms.TryGetValue(normalized, out var existing) && CompareKeys(existing.AdminKey, request.AdminKey)
            ? RestoreResult.Restored
            : RestoreResult.Taken;
    }

    public JoinStatus Rejoin(string code, string memberId, RejoinRequest request)
    {
        if (!RoomCode.TryValidate(code, out var normalized) || !_rooms.TryGetValue(normalized, out var entry)
            || !IsHex(memberId, MemberIdByteCount * 2) || !IsHex(request.Secret, MemberIdByteCount * 2))
        {
            return JoinStatus.UnknownRoom;
        }

        lock (entry.Lock)
        {
            if (IsDeadLocked(entry))
            {
                return JoinStatus.UnknownRoom;
            }

            if (entry.MemberSecrets.TryGetValue(memberId, out var expectedHash))
            {
                return CompareHash(expectedHash, request.Secret) ? JoinStatus.Joined : JoinStatus.UnknownRoom;
            }

            if (entry.Members.Count >= _options.MaxMembers)
            {
                return JoinStatus.RoomFull;
            }

            if (!IsValidName(request.DisplayName))
            {
                return JoinStatus.InvalidName;
            }

            var key = NameKey(request.DisplayName);
            if (entry.Members.Any(m => NameKey(m.DisplayName) == key))
            {
                return JoinStatus.NameTaken;
            }

            ValidateEndpoint(request.Endpoint);
            var now = _timeProvider.GetUtcNow();
            entry.Members.Add(new RoomMember(memberId, request.DisplayName, now, request.Endpoint));
            entry.MemberSecrets[memberId] = ComputeHash(request.Secret);
            entry.LastSeen[memberId] = now;
            return JoinStatus.Joined;
        }
    }

    private void RememberClosed(string code) => _closed[code] = _timeProvider.GetUtcNow() + _options.ClosedMemory;

    private bool WasClosed(string code)
    {
        if (!_closed.TryGetValue(code, out var until))
        {
            return false;
        }

        if (_timeProvider.GetUtcNow() < until)
        {
            return true;
        }

        _closed.TryRemove(code, out _);
        return false;
    }

    private static bool IsHex(string? value, int length)
        => value is not null && value.Length == length && value.All(Uri.IsHexDigit);

    public int ActiveRooms
    {
        get
        {
            var now = _timeProvider.GetUtcNow();
            var count = 0;
            foreach (var entry in _rooms.Values)
            {
                if (now < entry.ExpiresUtc)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public void SweepExpired()
    {
        foreach (var (roomCode, entry) in _rooms)
        {
            lock (entry.Lock)
            {
                if (IsDeadLocked(entry))
                {
                    _rooms.TryRemove(roomCode, out _);
                }
            }
        }
    }

    private bool IsDeadLocked(RoomEntry entry)
    {
        var now = _timeProvider.GetUtcNow();
        if (now >= entry.ExpiresUtc)
        {
            return true;
        }

        var cutoff = now - _options.MemberTimeout;
        foreach (var (memberId, lastSeen) in entry.LastSeen.ToList())
        {
            if (lastSeen < cutoff)
            {
                RemoveMemberLocked(entry, memberId);
            }
        }

        return !entry.MemberSecrets.ContainsKey(entry.HostMemberId);
    }

    private static void RemoveMemberLocked(RoomEntry entry, string memberId)
    {
        entry.Members.RemoveAll(m => m.MemberId == memberId);
        entry.MemberSecrets.Remove(memberId);
        entry.LastSeen.Remove(memberId);
    }

    private static void ValidateName(string displayName)
    {
        if (!IsValidName(displayName))
        {
            throw new ArgumentException("Display name must be 1-32 visible characters.", nameof(displayName));
        }
    }

    public static bool IsValidName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > MaxDisplayNameLength || displayName != displayName.Trim())
        {
            return false;
        }

        foreach (var c in displayName)
        {
            switch (char.GetUnicodeCategory(c))
            {
                case System.Globalization.UnicodeCategory.Control:
                case System.Globalization.UnicodeCategory.Format:
                case System.Globalization.UnicodeCategory.LineSeparator:
                case System.Globalization.UnicodeCategory.ParagraphSeparator:
                case System.Globalization.UnicodeCategory.PrivateUse:
                case System.Globalization.UnicodeCategory.OtherNotAssigned:
                    return false;
            }
        }

        return true;
    }

    private static string NameKey(string displayName)
        => displayName.Normalize(System.Text.NormalizationForm.FormKC).ToUpperInvariant();

    private static void ValidateEndpoint(EndpointInfo? endpoint)
    {
        if (endpoint is null)
        {
            return;
        }

        if (endpoint.Addresses.Count == 0 || endpoint.Port is < 1 or > 65535)
        {
            throw new ArgumentException("Endpoint must carry at least one address and a valid port.", nameof(endpoint));
        }

        if (endpoint.Addresses.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Endpoint addresses must be non-empty.", nameof(endpoint));
        }
    }

    private static RoomInfo Snapshot(RoomEntry entry) =>
        new(entry.RoomCode, entry.CreatedUtc, entry.ExpiresUtc, entry.Members.ToArray());

    private static bool CompareKeys(byte[] expected, string? provided)
    {
        var providedBytes = Encoding.ASCII.GetBytes(provided ?? string.Empty);
        return expected.Length == providedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expected, providedBytes);
    }

    private sealed class RoomEntry
    {
        public RoomEntry(string roomCode, byte[] adminKey, DateTimeOffset createdUtc, DateTimeOffset expiresUtc)
        {
            RoomCode = roomCode;
            AdminKey = adminKey;
            CreatedUtc = createdUtc;
            ExpiresUtc = expiresUtc;
        }

        public string RoomCode { get; }

        public byte[] AdminKey { get; }

        public string HostMemberId { get; init; } = string.Empty;

        public Dictionary<string, DateTimeOffset> LastSeen { get; } = new();

        public DateTimeOffset CreatedUtc { get; }

        public DateTimeOffset ExpiresUtc { get; }

        public object Lock { get; } = new();

        public List<RoomMember> Members { get; } = new();

        public Dictionary<string, byte[]> MemberSecrets { get; } = new();
    }
}
