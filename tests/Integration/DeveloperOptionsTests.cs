using CODConnect.Core.Rooms;
using CODConnect.Service;
using CODConnect.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CODConnect.IntegrationTests;

[Collection("rendezvous-env")]
public sealed class DeveloperOptionsTests
{
    private const string Default = "https://codconnect-rendezvous.onrender.com/";

    private static string SettingsPath() => Path.Combine(Path.GetTempPath(), "codconnect-tests", Guid.NewGuid().ToString("N"), "settings.json");

    private static byte[] Tcp(ushort port, byte flags)
    {
        var tcp = new byte[20];
        tcp[0] = 0xC3; tcp[1] = 0x50;
        tcp[2] = (byte)(port >> 8); tcp[3] = (byte)port;
        tcp[12] = 0x50; tcp[13] = flags;
        var ip = IPv4Packet.BuildPayload(new IPv4Address(10, 42, 0, 3), new IPv4Address(10, 42, 0, 2), IPv4Packet.ProtocolTcp, tcp);
        return EthernetFrame.Build(new MacAddress(0x02, 0, 0, 0, 0, 2), new MacAddress(0x02, 0, 0, 0, 0, 3), EthernetFrame.EtherTypeIpv4, ip);
    }

    [Fact]
    public void FriendsCannotOpenTheConsolesFtp_UnlessAllowed_GamesUnaffected()
    {
        var allowed = false;
        var guard = new RoomGuard(new RoomOptions(), isHost: false, () => allowed);

        foreach (var port in new ushort[] { 21, 2121, 1337, 9020, 9090, 12800 })
        {
            Assert.False(guard.AllowsFromTunnel(Tcp(port, 0x02)));
        }

        Assert.True(guard.AllowsFromTunnel(Tcp(3074, 0x02)));
        Assert.True(guard.AllowsFromTunnel(Tcp(21, 0x12)));
        Assert.True(guard.AllowsFromTunnel(Tcp(21, 0x10)));

        allowed = true;
        Assert.True(guard.AllowsFromTunnel(Tcp(21, 0x02)));
    }

    [Fact]
    public async Task RoomServer_MustAnswer_AndHttpsUnlessLocal_AndPersists()
    {
        var path = SettingsPath();
        var answering = new HashSet<string> { "https://mine.example/", "http://192.168.1.50:5090/" };
        DevSettings Create() => new(Default, path, (uri, _) => Task.FromResult(answering.Contains(uri.ToString())));

        var dev = Create();
        Assert.Equal(Default, dev.RoomServer);
        Assert.False(dev.FriendsConsoleAccess);
        Assert.False(dev.RelayOnly);

        await Assert.ThrowsAsync<ArgumentException>(() => dev.SetRoomServerAsync("http://mine.example"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dev.SetRoomServerAsync("https://typo.example"));
        Assert.Equal(Default, dev.RoomServer);

        await dev.SetRoomServerAsync("https://mine.example");
        dev.SetToggles(friendsConsoleAccess: true, relayOnly: true);
        Assert.Equal(new Uri("https://mine.example/"), dev.Rendezvous.BaseAddress);

        var reloaded = Create();
        Assert.Equal(("https://mine.example/", true, true), (reloaded.RoomServer, reloaded.FriendsConsoleAccess, reloaded.RelayOnly));

        await reloaded.SetRoomServerAsync("http://192.168.1.50:5090");
        await reloaded.SetRoomServerAsync(null);
        Assert.Equal(Default, Create().RoomServer);
    }

    [Fact]
    public async Task RelayOnly_RefusesAHostWithoutARelayAddress()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (_, hostEngine) = VirtualPairLink.CreatePair("hc", "he", new MacAddress(0x02, 0, 0, 0, 0, 0x0A), MacAddress.None);
        var (_, joinEngine) = VirtualPairLink.CreatePair("jc", "je", new MacAddress(0x02, 0, 0, 0, 0, 0x0B), MacAddress.None);
        await using var host = await RoomSession.HostAsync(new RoomSessionOptions
        {
            DisplayName = "Host",
            Rendezvous = factory.CreateClient(),
            Transport = new TcpRoomTransport { ListenPort = 0 },
            ConsoleFactory = () => Task.FromResult<IConsoleNetworkInterface>(hostEngine),
        });

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => RoomSession.JoinAsync(new RoomSessionOptions
        {
            DisplayName = "Kyle",
            RoomCode = host.RoomCode,
            Rendezvous = factory.CreateClient(),
            Transport = new TcpRoomTransport { ListenPort = 0 },
            ConsoleFactory = () => Task.FromResult<IConsoleNetworkInterface>(joinEngine),
            RelayOnly = true,
        }));
        Assert.Contains("relay", refused.Message);
    }
}
