using System.Collections.Concurrent;
using CODConnect.NetworkSimulation;
using CODConnect.Protocol;
using CODConnect.Sessions.Chat;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CODConnect.IntegrationTests;

public sealed class TunnelRoomChatTests
{
    private static readonly RoomMember Alex = new("aaaaaaaaaaaa", "Alex", DateTimeOffset.UtcNow);
    private static readonly RoomMember Sam = new("bbbbbbbbbbbb", "Sam", DateTimeOffset.UtcNow);

    private sealed class Pair : IAsyncDisposable
    {
        private int _dropNext;

        public Pair(IReadOnlyList<RoomMember>? members = null)
        {
            Members = members ?? [Alex, Sam];
            A = Create(Alex, () => B!);
            B = Create(Sam, () => A);
        }

        public IReadOnlyList<RoomMember> Members { get; set; }
        public TunnelRoomChat A { get; }
        public TunnelRoomChat B { get; }
        public bool Connected { get; set; } = true;
        public bool DuplicateEverything { get; set; }
        public ConcurrentQueue<byte[]> Wire { get; } = new();

        public void DropNextUnicast(int count) => Interlocked.Exchange(ref _dropNext, count);

        private TunnelRoomChat Create(RoomMember self, Func<TunnelRoomChat> other)
            => new($"room:{self.MemberId}", self.MemberId, self.DisplayName,
                _ => Task.FromResult(Members),
                (frame, _) =>
                {
                    var bytes = frame.ToArray();
                    Wire.Enqueue(bytes);
                    var unicast = (bytes[0] & 1) == 0;
                    if (!Connected || (unicast && Interlocked.Decrement(ref _dropNext) >= 0))
                    {
                        return ValueTask.CompletedTask;
                    }

                    other().ReceiveFrame(bytes);
                    if (DuplicateEverything)
                    {
                        other().ReceiveFrame(bytes);
                    }

                    return ValueTask.CompletedTask;
                },
                () => Connected,
                encrypted: () => true);

        public void Start()
        {
            A.Start();
            B.Start();
        }

        public async ValueTask DisposeAsync()
        {
            await A.DisposeAsync();
            await B.DisposeAsync();
        }
    }

    private static async Task Wait(Func<bool> ready, Func<string> detail, int seconds = 15)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (!ready() && DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(50);
        }

        Assert.True(ready(), detail());
    }

    private static Task Connected(Pair pair)
        => Wait(() => pair.A.Snapshot().Peers.Any(p => p.Connected) && pair.B.Snapshot().Peers.Any(p => p.Connected),
            () => $"A:{pair.A.Snapshot().State}; B:{pair.B.Snapshot().State}");

    [Fact]
    public async Task Messages_WithNamesAndReceipts_BothWays()
    {
        await using var pair = new Pair();
        pair.Start();
        await Connected(pair);
        Assert.Equal("Connected · encrypted room tunnel", pair.A.Snapshot().State);

        const string text = "Ready for the next match? こんにちは";
        pair.A.Send(text);
        await Wait(() => pair.B.Snapshot().Messages.Any(m => m.Text == text), () => "receive");
        await Wait(() => pair.A.Snapshot().Messages.Single().Delivery == "Delivered", () => "receipt");
        Assert.Equal("Alex", pair.B.Snapshot().Messages.Single().Author);
        Assert.False(pair.B.Snapshot().Messages.Single().Own);

        pair.B.Send("Yes, joining now.");
        await Wait(() => pair.A.Snapshot().Messages.Any(m => !m.Own && m.Author == "Sam"), () => "reply");
    }

    [Fact]
    public async Task Input_IsValidated_AndSendingNeedsAConnectedFriend()
    {
        await using var pair = new Pair();
        Assert.Throws<InvalidOperationException>(() => pair.A.Send("nobody here yet"));

        pair.Start();
        await Connected(pair);
        Assert.Throws<ArgumentException>(() => pair.B.Send(new string('x', TunnelRoomChat.MaxMessageLength + 1)));
        Assert.Throws<ArgumentException>(() => pair.B.Send("\u001bmalformed"));
        Assert.Throws<ArgumentException>(() => pair.B.Send("   "));

        pair.Connected = false;
        Assert.Throws<InvalidOperationException>(() => pair.A.Send("link is down"));
    }

    [Fact]
    public async Task LostFrames_AreRetransmitted_UntilDelivered()
    {
        await using var pair = new Pair();
        pair.Start();
        await Connected(pair);

        pair.DropNextUnicast(3);
        pair.A.Send("through the packet loss");

        await Wait(() => pair.B.Snapshot().Messages.Any(m => m.Text == "through the packet loss"), () => "retransmitted");
        await Wait(() => pair.A.Snapshot().Messages.Single().Delivery == "Delivered", () => "eventually acknowledged");
    }

    [Fact]
    public async Task DuplicateFrames_AreShownOnce()
    {
        await using var pair = new Pair { DuplicateEverything = true };
        pair.Start();
        await Connected(pair);

        pair.A.Send("once only");
        await Wait(() => pair.A.Snapshot().Messages.Single().Delivery == "Delivered", () => "delivered");
        await Task.Delay(300);
        Assert.Single(pair.B.Snapshot().Messages, m => m.Text == "once only");
    }

    [Fact]
    public async Task NonMember_IsIgnored()
    {
        await using var pair = new Pair(members: [Alex]);
        pair.Start();
        await Task.Delay(2500);

        Assert.Empty(pair.A.Snapshot().Peers);
        Assert.Throws<InvalidOperationException>(() => pair.A.Send("hello?"));
    }

    [Fact]
    public async Task Impostor_ClaimingAnExistingMember_IsIgnored()
    {
        await using var pair = new Pair();
        pair.Start();
        await Connected(pair);

        var hello = Forge(destination: [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], type: 1, payload: "bbbbbbbbbbbb"u8.ToArray());
        pair.A.ReceiveFrame(hello);
        Assert.Single(pair.A.Snapshot().Peers);

        var chatMacOfA = pair.Wire.First(f => f[0] == 0xFF).AsSpan(6, 6).ToArray();
        var message = new byte[8 + 5];
        "spoof"u8.CopyTo(message.AsSpan(8));
        pair.A.ReceiveFrame(Forge(chatMacOfA, 3, message));
        await Task.Delay(200);
        Assert.DoesNotContain(pair.A.Snapshot().Messages, m => m.Text == "spoof");
    }

    /// <summary>The room server forgetting the room (restart) must not make a friend who is still here "leave".</summary>
    [Fact]
    public async Task FriendStillTalking_StaysWhileTheRoomServerForgetsThem()
    {
        await using var pair = new Pair();
        pair.Start();
        await Connected(pair);

        pair.Members = [Alex];
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.Contains(pair.A.Snapshot().Peers, p => p.Name == "Sam" && p.Connected);
    }

    [Fact]
    public async Task MemberWhoLeaves_DropsOut()
    {
        await using var pair = new Pair();
        pair.Start();
        await Connected(pair);

        await pair.B.DisposeAsync();
        pair.Members = [Alex];
        await Wait(() => pair.A.Snapshot().Peers.Count == 0, () => "Sam should be gone", seconds: 25);
        Assert.Equal("Waiting for friend", pair.A.Snapshot().State);
    }

    [Fact]
    public async Task UnencryptedTransport_IsDisclosed()
    {
        var members = new[] { Alex, Sam };
        TunnelRoomChat? a = null, b = null;
        a = new TunnelRoomChat("r", Alex.MemberId, "Alex", _ => Task.FromResult<IReadOnlyList<RoomMember>>(members),
            (f, _) => { b!.ReceiveFrame(f); return ValueTask.CompletedTask; }, () => true, encrypted: () => false);
        b = new TunnelRoomChat("r", Sam.MemberId, "Sam", _ => Task.FromResult<IReadOnlyList<RoomMember>>(members),
            (f, _) => { a.ReceiveFrame(f); return ValueTask.CompletedTask; }, () => true, encrypted: () => false);
        await using var first = a;
        await using var second = b;
        a.Start();
        b.Start();

        await Wait(() => a.Snapshot().Peers.Any(p => p.Connected), () => a.Snapshot().State);
        Assert.Equal("Connected · local link, not encrypted", a.Snapshot().State);
    }

    [Fact]
    public async Task RoomSessions_Chat_AndChatFramesNeverReachConsoles()
    {
        using var factory = new WebApplicationFactory<Program>();
        var hostMac = new MacAddress(0x02, 0, 0, 0, 0, 0x0A);
        var joinMac = new MacAddress(0x02, 0, 0, 0, 0, 0x0B);
        var (hostConsole, hostEngine) = VirtualPairLink.CreatePair("hc", "he", hostMac, MacAddress.None);
        var (joinConsole, joinEngine) = VirtualPairLink.CreatePair("jc", "je", joinMac, MacAddress.None);

        await using var host = await CODConnect.Sessions.RoomSession.HostAsync(new CODConnect.Sessions.RoomSessionOptions
        {
            DisplayName = "Alex",
            Rendezvous = factory.CreateClient(),
            Transport = new CODConnect.Sessions.TcpRoomTransport { ListenPort = 0 },
            ConsoleFactory = () => Task.FromResult<IConsoleNetworkInterface>(hostEngine),
        });
        await using var joiner = await CODConnect.Sessions.RoomSession.JoinAsync(new CODConnect.Sessions.RoomSessionOptions
        {
            DisplayName = "Sam",
            RoomCode = host.RoomCode,
            Rendezvous = factory.CreateClient(),
            Transport = new CODConnect.Sessions.TcpRoomTransport { ListenPort = 0 },
            ConsoleFactory = () => Task.FromResult<IConsoleNetworkInterface>(joinEngine),
        });

        await Wait(() => host.Chat!.Snapshot().Peers.Any(p => p.Connected) && joiner.Chat!.Snapshot().Peers.Any(p => p.Connected),
            () => $"host:{host.Chat!.Snapshot().State} joiner:{joiner.Chat!.Snapshot().State}", seconds: 20);
        Assert.Equal("Connected · local link, not encrypted", host.Chat!.Snapshot().State);

        joiner.Chat!.Send("gg");
        await Wait(() => host.Chat!.Snapshot().Messages.Any(m => m.Text == "gg" && m.Author == "Sam"), () => "chat crossed the room");

        foreach (var console in new[] { hostConsole, joinConsole })
        {
            while (console.CaptureFrame() is { } frame)
            {
                Assert.False(frame.Buffer.Length >= 14 && frame.Buffer.Span[12] == 0x88 && frame.Buffer.Span[13] == 0xB5,
                    "a chat frame reached a console");
            }
        }

        Assert.DoesNotContain(host.Devices, d => d.Side == CODConnect.Sessions.DeviceSide.Remote && d.Mac != joinMac);
    }

    private static byte[] Forge(byte[] destination, byte type, byte[] payload)
    {
        var frame = new byte[Math.Max(60, 25 + payload.Length)];
        destination.CopyTo(frame, 0);
        new byte[] { 0x02, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE }.CopyTo(frame, 6);
        frame[12] = 0x88;
        frame[13] = 0xB5;
        "CODCHAT2"u8.CopyTo(frame.AsSpan(14));
        frame[22] = type;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(23, 2), (ushort)payload.Length);
        payload.CopyTo(frame, 25);
        return frame;
    }

    [Fact]
    public async Task Hello_CarriesEachPlayersConsole_AndMembersWithoutAPcStayUnconnected()
    {
        var kim = new RoomMember("cccccccccccc", "Kim", DateTimeOffset.UtcNow);
        IReadOnlyList<RoomMember> members = [Alex, Sam, kim];
        byte[] samConsole = [0x28, 0x66, 0xE3, 0x51, 0x7B, 0x01];
        TunnelRoomChat? a = null, b = null;
        a = new TunnelRoomChat("room:a", Alex.MemberId, Alex.DisplayName, _ => Task.FromResult(members),
            (frame, _) => { b!.ReceiveFrame(frame.ToArray()); return ValueTask.CompletedTask; }, () => true);
        b = new TunnelRoomChat("room:b", Sam.MemberId, Sam.DisplayName, _ => Task.FromResult(members),
            (frame, _) => { a.ReceiveFrame(frame.ToArray()); return ValueTask.CompletedTask; }, () => true,
            self: () => (samConsole, true, "PS4"));
        await using var _ = a;
        await using var __ = b;
        a.Start();
        b.Start();

        await Wait(() => a.Snapshot().Peers.Any(p => p.Name == "Sam" && p.Connected && p.ConsoleMac is not null),
            () => string.Join(", ", a.Snapshot().Peers));

        var peers = a.Snapshot().Peers;
        Assert.Equal(["Sam", "Kim"], peers.Select(p => p.Name));
        var sam = peers[0];
        Assert.Equal(("28:66:E3:51:7B:01", true), (sam.ConsoleMac, sam.Relay));
        Assert.Equal("PS4", a.ConsoleNameOf(Sam.MemberId));
        Assert.False(peers[1].Connected);
        Assert.Null(peers[1].ConsoleMac);

        await Wait(() => b.Snapshot().Peers.Any(p => p.Name == "Alex" && p.Connected), () => string.Join(", ", b.Snapshot().Peers));
        Assert.Null(b.Snapshot().Peers.Single(p => p.Name == "Alex").ConsoleMac);
    }
}
