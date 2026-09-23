using CODConnect.Protocol;
using CODConnect.Rendezvous;
using Xunit;

namespace CODConnect.Rendezvous.Tests;

public class RoomStoreTests
{
    private static readonly DateTimeOffset StartUtc = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly EndpointInfo TestEndpoint = new(["192.168.1.10"], 47777);

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = StartUtc;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    [Fact]
    public void Create_Join_Get_FullLifecycle()
    {
        var time = new FakeTimeProvider();
        var store = new RoomStore(new RoomStoreOptions { RoomTtl = TimeSpan.FromHours(2), MaxMembers = 8 }, time);

        var created = store.Create("host", TestEndpoint);
        Assert.Equal(StartUtc + TimeSpan.FromHours(2), created.ExpiresUtc);
        Assert.Equal(1, store.ActiveRooms);

        var join = store.Join(created.RoomCode, "alice", null);
        Assert.Equal(JoinStatus.Joined, join.Status);
        Assert.NotNull(join.Response);
        Assert.NotNull(join.RoomInfo);
        Assert.Matches("^[0-9a-f]{12}$", join.Response!.MemberId);
        Assert.Equal(created.RoomCode, join.Response.RoomCode);
        Assert.Equal(created.ExpiresUtc, join.Response.ExpiresUtc);
        var member = join.RoomInfo!.Members.Single(m => m.MemberId == join.Response.MemberId);
        Assert.Equal("alice", member.DisplayName);
        Assert.Equal(StartUtc, member.JoinedUtc);
        Assert.Null(member.Endpoint);

        var info = store.Get(created.RoomCode);
        Assert.NotNull(info);
        Assert.Equal(created.RoomCode, info!.RoomCode);
        Assert.Equal(2, info.MemberCount);
        Assert.Equal("host", info.Members[0].DisplayName);
        Assert.Equal(TestEndpoint, info.Members[0].Endpoint);
        Assert.Equal("alice", info.Members[1].DisplayName);
        Assert.Equal(created.ExpiresUtc, info.ExpiresUtc);
        Assert.Equal(StartUtc, info.CreatedUtc);
        Assert.Equal(1, store.ActiveRooms);

        var second = store.Join(created.RoomCode, "bob", null);
        Assert.Equal(JoinStatus.Joined, second.Status);
        Assert.Equal(3, second.RoomInfo!.MemberCount);
        Assert.Equal(3, store.Get(created.RoomCode)!.MemberCount);
    }

    [Fact]
    public void Join_ReturnsUnknownRoom_ForMalformedOrUnknownCodes()
    {
        var store = new RoomStore(new RoomStoreOptions(), new FakeTimeProvider());
        var created = store.Create("host", TestEndpoint);

        Assert.Equal(JoinStatus.UnknownRoom, store.Join("", "alice", null).Status);
        Assert.Equal(JoinStatus.UnknownRoom, store.Join("not a code!", "alice", null).Status);
        Assert.Equal(JoinStatus.UnknownRoom, store.Join("0000-00", "alice", null).Status);

        Assert.Equal(JoinStatus.UnknownRoom, store.Join("ZZZZ-ZZ", "alice", null).Status);

        Assert.True(store.Close(created.RoomCode, created.AdminKey));
        Assert.Equal(JoinStatus.UnknownRoom, store.Join(created.RoomCode, "alice", null).Status);
    }

    [Fact]
    public void Join_NormalizesCaseAndMissingHyphen()
    {
        var store = new RoomStore(new RoomStoreOptions(), new FakeTimeProvider());
        var created = store.Create("host", TestEndpoint);

        var squashed = created.RoomCode.Replace("-", string.Empty).ToLowerInvariant();
        var outcome = store.Join(squashed, "alice", null);

        Assert.Equal(JoinStatus.Joined, outcome.Status);
        Assert.Equal(created.RoomCode, outcome.Response!.RoomCode);
        Assert.Equal(2, store.Get(created.RoomCode)!.MemberCount);
    }

    [Fact]
    public void Join_ReturnsRoomFull_AtMaxMembers()
    {
        var store = new RoomStore(new RoomStoreOptions { MaxMembers = 2 }, new FakeTimeProvider());
        var created = store.Create("host", TestEndpoint);
        Assert.Equal(1, store.Get(created.RoomCode)!.MemberCount);

        Assert.Equal(JoinStatus.Joined, store.Join(created.RoomCode, "a", null).Status);

        var overflow = store.Join(created.RoomCode, "b", null);
        Assert.Equal(JoinStatus.RoomFull, overflow.Status);
        Assert.Null(overflow.Response);
        Assert.Null(overflow.RoomInfo);
        Assert.Equal(2, store.Get(created.RoomCode)!.MemberCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \n")]
    public void Join_ReturnsInvalidName_ForBlankDisplayNames(string displayName)
    {
        var store = new RoomStore(new RoomStoreOptions(), new FakeTimeProvider());
        var created = store.Create("host", TestEndpoint);

        var outcome = store.Join(created.RoomCode, displayName, null);

        Assert.Equal(JoinStatus.InvalidName, outcome.Status);
        Assert.Null(outcome.Response);
        Assert.Null(outcome.RoomInfo);
        Assert.Equal(1, store.Get(created.RoomCode)!.MemberCount);
    }

    [Fact]
    public void Join_RejectsNamesOver7Chars_ButAccepts7()
    {
        var store = new RoomStore(new RoomStoreOptions(), new FakeTimeProvider());
        var created = store.Create("host", TestEndpoint);

        Assert.Equal(JoinStatus.InvalidName, store.Join(created.RoomCode, new string('x', 8), null).Status);
        Assert.Equal(JoinStatus.InvalidName, store.Join(created.RoomCode, new string('x', 100), null).Status);
        Assert.Equal(1, store.Get(created.RoomCode)!.MemberCount);

        Assert.Equal(JoinStatus.Joined, store.Join(created.RoomCode, new string('x', 7), null).Status);
        Assert.Equal(2, store.Get(created.RoomCode)!.MemberCount);
    }

    [Fact]
    public void Close_WithWrongKey_ReturnsFalseAndKeepsRoom()
    {
        var store = new RoomStore(new RoomStoreOptions(), new FakeTimeProvider());
        var created = store.Create("host", TestEndpoint);

        Assert.False(store.Close(created.RoomCode, "0123456789ABCDEF"));
        Assert.False(store.Close(created.RoomCode, "tooshort"));
        Assert.False(store.Close(created.RoomCode, string.Empty));
        Assert.False(store.Close(created.RoomCode, created.AdminKey.ToLowerInvariant()));
        Assert.NotNull(store.Get(created.RoomCode));
        Assert.Equal(1, store.ActiveRooms);
    }

    [Fact]
    public void Close_WithCorrectKey_RemovesRoom()
    {
        var store = new RoomStore(new RoomStoreOptions(), new FakeTimeProvider());
        var created = store.Create("host", TestEndpoint);

        Assert.True(store.Close(created.RoomCode, created.AdminKey));
        Assert.Null(store.Get(created.RoomCode));
        Assert.Equal(0, store.ActiveRooms);
        Assert.Equal(JoinStatus.UnknownRoom, store.Join(created.RoomCode, "alice", null).Status);
        Assert.False(store.Close(created.RoomCode, created.AdminKey));
        Assert.False(store.Close("ZZZZ-ZZ", created.AdminKey));
    }

    [Fact]
    public void ExpiredRooms_AreInvisible_JoinRejected_AndRemovedBySweep()
    {
        var time = new FakeTimeProvider();
        var ttl = TimeSpan.FromMinutes(30);
        var store = new RoomStore(new RoomStoreOptions { RoomTtl = ttl }, time);

        var expired = store.Create("host", TestEndpoint);
        time.Advance(ttl + TimeSpan.FromSeconds(1));
        var live = store.Create("host", TestEndpoint);

        Assert.Equal(1, store.ActiveRooms);

        Assert.Null(store.Get(expired.RoomCode));
        Assert.Equal(JoinStatus.UnknownRoom, store.Join(expired.RoomCode, "late", null).Status);
        Assert.NotNull(store.Get(live.RoomCode));
        Assert.Equal(1, store.ActiveRooms);

        store.SweepExpired();
        Assert.Equal(1, store.ActiveRooms);
        Assert.NotNull(store.Get(live.RoomCode));

        store.SweepExpired();
        Assert.Equal(1, store.ActiveRooms);
    }

    [Fact]
    public void Create_GeneratesValidCodes_AndSecretHexKeys()
    {
        var store = new RoomStore(new RoomStoreOptions(), new FakeTimeProvider());
        var codes = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 25; i++)
        {
            var created = store.Create("host", TestEndpoint);

            Assert.True(RoomCode.TryValidate(created.RoomCode, out var normalized));
            Assert.Equal(normalized, created.RoomCode);
            Assert.Equal(7, created.RoomCode.Length);
            Assert.DoesNotContain(created.RoomCode, c => c != '-' && "0O1IL".Contains(c));
            Assert.Matches("^[0-9A-F]{16}$", created.AdminKey);
            Assert.True(codes.Add(created.RoomCode));
            Assert.Equal(i + 1, store.ActiveRooms);
        }
    }

    private static (RoomStore Store, FakeTimeProvider Time) LeaseStore(int maxMembers = 8)
    {
        var time = new FakeTimeProvider();
        return (new RoomStore(new RoomStoreOptions { MaxMembers = maxMembers, MemberTimeout = TimeSpan.FromSeconds(60) }, time), time);
    }

    [Fact]
    public void Heartbeat_KeepsMembersAlive_PastTheTimeout()
    {
        var (store, time) = LeaseStore();
        var host = store.Create("host", TestEndpoint);
        var joiner = store.Join(host.RoomCode, "friend", null).Response!;

        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(40));
            Assert.Equal(HeartbeatStatus.Alive, store.Heartbeat(host.RoomCode, host.MemberId, host.MemberSecret));
            Assert.Equal(HeartbeatStatus.Alive, store.Heartbeat(host.RoomCode, joiner.MemberId, joiner.MemberSecret));
        }

        Assert.Equal(2, store.Get(host.RoomCode)!.MemberCount);
    }

    [Fact]
    public void SilentHost_ClosesTheRoom()
    {
        var (store, time) = LeaseStore();
        var host = store.Create("host", TestEndpoint);
        var joiner = store.Join(host.RoomCode, "friend", null).Response!;

        time.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(HeartbeatStatus.Alive, store.Heartbeat(host.RoomCode, joiner.MemberId, joiner.MemberSecret));
        time.Advance(TimeSpan.FromSeconds(40));

        Assert.Equal(HeartbeatStatus.RoomGone, store.Heartbeat(host.RoomCode, joiner.MemberId, joiner.MemberSecret));
        Assert.Null(store.Get(host.RoomCode));
        Assert.Equal(JoinStatus.UnknownRoom, store.Join(host.RoomCode, "late", null).Status);

        store.SweepExpired();
        Assert.Equal(0, store.ActiveRooms);
    }

    [Fact]
    public void SilentJoiner_IsDropped_AndItsSlotFreed()
    {
        var (store, time) = LeaseStore(maxMembers: 2);
        var host = store.Create("host", TestEndpoint);
        var joiner = store.Join(host.RoomCode, "friend", null).Response!;
        Assert.Equal(JoinStatus.RoomFull, store.Join(host.RoomCode, "third", null).Status);

        time.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(HeartbeatStatus.Alive, store.Heartbeat(host.RoomCode, host.MemberId, host.MemberSecret));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HeartbeatStatus.Alive, store.Heartbeat(host.RoomCode, host.MemberId, host.MemberSecret));

        Assert.Equal(1, store.Get(host.RoomCode)!.MemberCount);
        Assert.Equal(HeartbeatStatus.MemberGone, store.Heartbeat(host.RoomCode, joiner.MemberId, joiner.MemberSecret));
        Assert.Equal(JoinStatus.Joined, store.Join(host.RoomCode, "rejoin", null).Status);
    }

    [Fact]
    public void Leave_FreesTheSlot_ForRepeatedRejoins()
    {
        var (store, _) = LeaseStore(maxMembers: 2);
        var host = store.Create("host", TestEndpoint);

        for (var i = 0; i < 10; i++)
        {
            var outcome = store.Join(host.RoomCode, "friend", null);
            Assert.Equal(JoinStatus.Joined, outcome.Status);
            Assert.True(store.Leave(host.RoomCode, outcome.Response!.MemberId, outcome.Response.MemberSecret));
        }

        Assert.Equal(1, store.Get(host.RoomCode)!.MemberCount);
    }

    [Fact]
    public void Leave_WithWrongSecret_IsRefused()
    {
        var (store, _) = LeaseStore();
        var host = store.Create("host", TestEndpoint);
        var joiner = store.Join(host.RoomCode, "friend", null).Response!;

        Assert.False(store.Leave(host.RoomCode, joiner.MemberId, "wrong"));
        Assert.Equal(HeartbeatStatus.Forbidden, store.Heartbeat(host.RoomCode, joiner.MemberId, "wrong"));
        Assert.Equal(2, store.Get(host.RoomCode)!.MemberCount);
    }

    [Fact]
    public void HostLeaving_ClosesTheRoom()
    {
        var (store, _) = LeaseStore();
        var host = store.Create("host", TestEndpoint);
        store.Join(host.RoomCode, "friend", null);

        Assert.True(store.Leave(host.RoomCode, host.MemberId, host.MemberSecret));
        Assert.Null(store.Get(host.RoomCode));
    }
}
