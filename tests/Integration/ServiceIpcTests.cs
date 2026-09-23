using System.IO.Pipes;
using CODConnect.NetworkSimulation;
using CODConnect.Protocol;
using CODConnect.Service;
using CODConnect.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CODConnect.IntegrationTests;

public class ServiceIpcTests
{
    private static readonly MacAddress HostConsoleMac = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x0A);
    private static readonly MacAddress JoinerConsoleMac = new(0x02, 0x00, 0x00, 0x00, 0x00, 0x0B);

    private static async Task<T?> WaitForAsync<T>(Func<T?> probe, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = probe();
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(5);
        }

        return default;
    }

    private static (SessionManager Host, SessionManager Joiner, VirtualPairLink HostConsole, VirtualPairLink JoinerConsole) CreateManagers(HttpClient rendezvous)
    {
        var (hostConsole, hostEngineConsole) = VirtualPairLink.CreatePair("host-console", "host-engine", HostConsoleMac, MacAddress.None);
        var (joinerConsole, joinerEngineConsole) = VirtualPairLink.CreatePair("joiner-console", "joiner-engine", JoinerConsoleMac, MacAddress.None);

        var host = new SessionManager(
            (_, _) => Task.FromResult<IConsoleNetworkInterface>(hostEngineConsole),
            new TcpRoomTransport { ListenPort = 0 },
            rendezvous);
        var joiner = new SessionManager(
            (_, _) => Task.FromResult<IConsoleNetworkInterface>(joinerEngineConsole),
            new TcpRoomTransport { ListenPort = 0 },
            rendezvous);
        return (host, joiner, hostConsole, joinerConsole);
    }

    [Fact]
    public async Task Handler_HostJoinStatusStop_FullLifecycle()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (hostManager, joinerManager, hostConsole, joinerConsole) = CreateManagers(factory.CreateClient());
        await using var _ = hostManager;
        await using var __ = joinerManager;

        var hostHandler = new ServiceRequestHandler(hostManager, () => [new IpcAdapterInfo(0, "test-adapter", HostConsoleMac.ToString(), ["10.42.0.1"], "Up", false)]);

        var hostResponse = await hostHandler.HandleAsync(new IpcRequest("host", DisplayName: "HostA", Adapter: "test-adapter"));
        Assert.True(hostResponse.Ok, hostResponse.Error);
        Assert.NotNull(hostResponse.RoomCode);

        var joinerHandler = new ServiceRequestHandler(joinerManager, () => []);
        var joinResponse = await joinerHandler.HandleAsync(new IpcRequest("join", DisplayName: "PlayerB", RoomCode: hostResponse.RoomCode));
        Assert.True(joinResponse.Ok, joinResponse.Error);

        var arpRequest = ArpPacket.BuildEthernetFrame(JoinerConsoleMac, new IPv4Address(10, 42, 0, 11), new IPv4Address(10, 42, 0, 12), isReply: false, replyDestinationMac: MacAddress.None);
        await joinerConsole.InjectFrameAsync(arpRequest);
        var received = await WaitForAsync(() => hostConsole.CaptureFrame());
        Assert.NotNull(received);

        var status = await hostHandler.HandleAsync(new IpcRequest("status"));
        Assert.True(status.Ok);
        Assert.NotNull(status.Status);
        Assert.True(status.Status!.HasSession);
        Assert.Equal("Host", status.Status.Role);
        Assert.Equal(hostResponse.RoomCode, status.Status.RoomCode);
        Assert.Equal("Connected", status.Status.TunnelState);

        var stop = await hostHandler.HandleAsync(new IpcRequest("stop"));
        Assert.True(stop.Ok);
        Assert.False((await hostHandler.HandleAsync(new IpcRequest("status"))).Status!.HasSession);

        var unknown = await hostHandler.HandleAsync(new IpcRequest("bogus"));
        Assert.False(unknown.Ok);
        Assert.Contains("unknown op", unknown.Error);
    }

    [Fact]
    public void Handler_RejectsSecondSession_WhileOneIsActive()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (hostManager, _, _, _) = CreateManagers(factory.CreateClient());
        var handler = new ServiceRequestHandler(hostManager, () => []);

        var first = handler.HandleAsync(new IpcRequest("host", DisplayName: "A")).GetAwaiter().GetResult();
        Assert.True(first.Ok, first.Error);
        try
        {
            var second = handler.HandleAsync(new IpcRequest("host", DisplayName: "B")).GetAwaiter().GetResult();
            Assert.False(second.Ok);
            Assert.Contains("already running", second.Error);
        }
        finally
        {
            hostManager.StopAsync().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task PipeServer_RoundTripsRequests_IncludingTwoOnOneConnection()
    {
        var pipeName = $"codconnect.test.{Guid.NewGuid():N}";
        var handler = new StubHandler();
        await using var server = new ServicePipeServer(handler, pipeName, log: message => Console.WriteLine("PIPE: " + message));
        await server.StartAsync();

        var client = new ServiceIpcClient(pipeName);
        await using var _ = client;

        var first = await client.SendAsync(new IpcRequest("ping"));
        Assert.True(first.Ok);
        Assert.Equal("ping-ack", first.Error);

        var second = await client.SendAsync(new IpcRequest("echo-me"));
        Assert.True(second.Ok);
        Assert.Equal("echo:echo-me", second.Error);

        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await IpcWire.WriteRequestAsync(writer, new IpcRequest("ping"));
        var response1 = await IpcWire.ReadResponseAsync(reader);
        Assert.NotNull(response1);
        await IpcWire.WriteRequestAsync(writer, new IpcRequest("echo-me"));
        var response2 = await IpcWire.ReadResponseAsync(reader);
        Assert.NotNull(response2);
        Assert.Equal("echo:echo-me", response2!.Error);
    }

    private sealed class StubHandler : IIpcHandler
    {
        public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(request.Op switch
            {
                "ping" => new IpcResponse(true, Error: "ping-ack"),
                _ => new IpcResponse(true, Error: $"echo:{request.Op}"),
            });
    }
    [Fact]
    public void PipeSecurity_AllowsInteractiveUsers_DeniesNetworkLogons()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var rules = ServicePipeServer.BuildPipeSecurity()
            .GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();

        var interactive = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.InteractiveSid, null);
        var network = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.NetworkSid, null);

        var interactiveRule = Assert.Single(rules, r => r.IdentityReference.Equals(interactive));
        Assert.Equal(System.Security.AccessControl.AccessControlType.Allow, interactiveRule.AccessControlType);
        Assert.Equal(PipeAccessRights.ReadWrite, interactiveRule.PipeAccessRights & PipeAccessRights.ReadWrite);
        Assert.Equal(0, (int)(interactiveRule.PipeAccessRights & (PipeAccessRights.ChangePermissions | PipeAccessRights.TakeOwnership)));

        var networkRule = Assert.Single(rules, r => r.IdentityReference.Equals(network));
        Assert.Equal(System.Security.AccessControl.AccessControlType.Deny, networkRule.AccessControlType);
    }
}
