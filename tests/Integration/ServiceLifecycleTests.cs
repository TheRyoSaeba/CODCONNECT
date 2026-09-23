using CODConnect.Rendezvous;
using Microsoft.Extensions.DependencyInjection;
using CODConnect.NetworkSimulation;
using CODConnect.Protocol;
using CODConnect.Service;
using CODConnect.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CODConnect.IntegrationTests;

public class ServiceLifecycleTests
{
    private static readonly MacAddress HostMac = new(0x02, 0, 0, 0, 0, 0x1A);
    private static readonly MacAddress JoinMac = new(0x02, 0, 0, 0, 0, 0x1B);

    private static string TempJournal() => Path.Combine(Path.GetTempPath(), "codconnect-tests", Guid.NewGuid().ToString("N"), "session.bin");

    private static SessionManager Manager(HttpClient rendezvous, MacAddress mac, SessionJournal? journal)
    {
        var (_, engine) = VirtualPairLink.CreatePair($"c{mac}", $"e{mac}", mac, MacAddress.None);
        return new SessionManager((_, _) => Task.FromResult<IConsoleNetworkInterface>(engine), new TcpRoomTransport { ListenPort = 0 }, rendezvous, journal: journal);
    }

    [Fact]
    public async Task HostCrash_NextStartClosesTheRoom()
    {
        using var factory = new WebApplicationFactory<Program>();
        var path = TempJournal();
        var crashed = Manager(factory.CreateClient(), HostMac, new SessionJournal(path));
        var code = await crashed.StartHostAsync("Host", adapter: null);
        var rooms = factory.Services.GetRequiredService<RoomStore>();
        Assert.NotNull(rooms.Get(code));

        await new SessionJournal(path).RecoverAsync(factory.CreateClient());

        Assert.Null(rooms.Get(code));
        Assert.Null(new SessionJournal(path).Read());
        await crashed.DisposeAsync();
    }

    [Fact]
    public async Task JoinerCrash_NextStartLeavesTheRoom()
    {
        using var factory = new WebApplicationFactory<Program>();
        await using var host = Manager(factory.CreateClient(), HostMac, journal: null);
        var code = await host.StartHostAsync("Host", adapter: null);

        var path = TempJournal();
        var crashed = Manager(factory.CreateClient(), JoinMac, new SessionJournal(path));
        await crashed.StartJoinAsync(code, "Friend", adapter: null);
        var rooms = factory.Services.GetRequiredService<RoomStore>();
        Assert.Equal(2, rooms.Get(code)!.MemberCount);

        await new SessionJournal(path).RecoverAsync(factory.CreateClient());

        Assert.Equal(1, rooms.Get(code)!.MemberCount);
        await crashed.DisposeAsync();
    }

    [Fact]
    public async Task ServiceStop_EndsTheRoom_AndClearsTheJournal()
    {
        using var factory = new WebApplicationFactory<Program>();
        var path = TempJournal();
        var journal = new SessionJournal(path);
        var sessions = Manager(factory.CreateClient(), HostMac, journal);
        var lifecycle = new SessionLifecycleHostedService(sessions, journal, factory.CreateClient());

        await lifecycle.StartAsync(CancellationToken.None);
        var code = await sessions.StartHostAsync("Host", adapter: null);
        Assert.NotNull(journal.Read());

        await lifecycle.StopAsync(CancellationToken.None);

        Assert.False(sessions.HasSession);
        Assert.Null(factory.Services.GetRequiredService<RoomStore>().Get(code));
        Assert.Null(journal.Read());
    }

    [Fact]
    public async Task Recovery_WithUnreachableRendezvous_StillClearsTheJournal()
    {
        var path = TempJournal();
        var journal = new SessionJournal(path);
        journal.Write(new SessionRecord("ABCD-EF", "member", "secret", "adminkey"));

        using var dead = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/"), Timeout = TimeSpan.FromSeconds(2) };
        await journal.RecoverAsync(dead);

        Assert.Null(journal.Read());
        await new SessionJournal(TempJournal()).RecoverAsync(dead);
    }
}
