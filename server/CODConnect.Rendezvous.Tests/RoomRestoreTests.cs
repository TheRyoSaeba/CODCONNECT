using CODConnect.Protocol;
using CODConnect.Rendezvous;
using Xunit;

namespace CODConnect.Rendezvous.Tests;

public class RoomRestoreTests
{
    private static readonly EndpointInfo Endpoint = new(["vpn1.softether.net"], 5555);

    [Fact]
    public void AfterARestart_TheHostPutsTheRoomBack_AndFriendsRejoinAsThemselves()
    {
        var before = new RoomStore();
        var host = before.Create("Host", Endpoint);
        var kyle = before.Join(host.RoomCode, "Kyle", null).Response!;

        var after = new RoomStore();
        Assert.Equal(HeartbeatStatus.RoomGone, after.Heartbeat(host.RoomCode, host.MemberId, host.MemberSecret));
        Assert.Equal(JoinStatus.UnknownRoom, after.Rejoin(host.RoomCode, kyle.MemberId, new RejoinRequest(kyle.MemberSecret, "Kyle")));

        Assert.Equal(RestoreResult.Restored, after.Restore(host.RoomCode, new RestoreRoomRequest(host.AdminKey, host.MemberId, host.MemberSecret, "Host", Endpoint)));
        Assert.Equal(RestoreResult.Restored, after.Restore(host.RoomCode, new RestoreRoomRequest(host.AdminKey, host.MemberId, host.MemberSecret, "Host", Endpoint)));
        Assert.Equal(JoinStatus.Joined, after.Rejoin(host.RoomCode, kyle.MemberId, new RejoinRequest(kyle.MemberSecret, "Kyle")));
        Assert.Equal(JoinStatus.Joined, after.Rejoin(host.RoomCode, kyle.MemberId, new RejoinRequest(kyle.MemberSecret, "Kyle")));

        Assert.Equal(HeartbeatStatus.Alive, after.Heartbeat(host.RoomCode, host.MemberId, host.MemberSecret));
        Assert.Equal(HeartbeatStatus.Alive, after.Heartbeat(host.RoomCode, kyle.MemberId, kyle.MemberSecret));
        Assert.Equal(["Host", "Kyle"], after.Get(host.RoomCode)!.Members.Select(m => m.DisplayName));
        Assert.True(after.Close(host.RoomCode, host.AdminKey)); // the original admin key still works
    }

    [Fact]
    public void AClosedRoom_EndsForEveryone_AndCannotBeRestored()
    {
        var store = new RoomStore();
        var host = store.Create("Host", Endpoint);
        var kyle = store.Join(host.RoomCode, "Kyle", null).Response!;

        Assert.True(store.Close(host.RoomCode, host.AdminKey));

        Assert.Equal(HeartbeatStatus.RoomClosed, store.Heartbeat(host.RoomCode, kyle.MemberId, kyle.MemberSecret));
        Assert.Equal(RestoreResult.Closed, store.Restore(host.RoomCode, new RestoreRoomRequest(host.AdminKey, host.MemberId, host.MemberSecret, "Host", Endpoint)));
    }

    [Fact]
    public void SomeoneElsesRoomCode_IsNeverTakenOver()
    {
        var store = new RoomStore();
        var host = store.Create("Host", Endpoint);

        var impostor = new RestoreRoomRequest("0123456789ABCDEF", "aaaaaaaaaaaa", "bbbbbbbbbbbb", "Impostor", Endpoint);
        Assert.Equal(RestoreResult.Taken, store.Restore(host.RoomCode, impostor));
        Assert.Equal(["Host"], store.Get(host.RoomCode)!.Members.Select(m => m.DisplayName));
    }

    [Fact]
    public void RoomsLastADay_WhileTheHostIsThere()
        => Assert.True(new RoomStore().Create("Host", Endpoint).ExpiresUtc - DateTimeOffset.UtcNow > TimeSpan.FromHours(23));
}
