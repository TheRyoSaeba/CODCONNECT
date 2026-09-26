using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CODConnect.Sessions.Chat;

public sealed class TunnelRoomChat : IAsyncDisposable
{
    public const int MaxMessageLength = 320;
    private const int MaxHistory = 80;
    private const int Header = 25;
    private const int MaxPeers = 7;
    private const byte TypeHello = 1, TypeMessage = 3, TypeAck = 4, TypeTyping = 5;
    private const int MaxConsoleName = 16;
    private static readonly byte[] Broadcast = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
    private static ReadOnlySpan<byte> Magic => "CODCHAT2"u8;
    private static readonly TimeSpan HelloInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TypingShownFor = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TypingResendAfter = TimeSpan.FromSeconds(2.5);

    private readonly string _roomId, _memberId, _name;
    private readonly Func<CancellationToken, Task<IReadOnlyList<RoomMember>>> _getMembers;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _sendFrame;
    private readonly Func<bool> _linkUp;
    private readonly Func<bool> _encrypted;
    private readonly Func<(byte[]? ConsoleMac, bool Relay, string? Console)> _self;
    private readonly Func<DateTimeOffset> _clock;
    private readonly byte[] _mac;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<ChatMessage> _messages = [];
    private readonly Dictionary<string, Peer> _peers = [];
    private readonly Dictionary<ulong, Outgoing> _pending = [];
    private IReadOnlyList<RoomMember> _members = [];
    private Task? _loop, _membershipLoop;
    private long _nextLocalId;
    private ulong _nextWireId;
    private DateTimeOffset _lastSend = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTyping = DateTimeOffset.MinValue;
    private bool _started;
    private int _disposed;

    public TunnelRoomChat(
        string roomId,
        string memberId,
        string name,
        Func<CancellationToken, Task<IReadOnlyList<RoomMember>>> getMembers,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendFrame,
        Func<bool> linkUp,
        Func<bool>? encrypted = null,
        Func<DateTimeOffset>? clock = null,
        Func<(byte[]? ConsoleMac, bool Relay, string? Console)>? self = null)
    {
        _self = self ?? (() => (null, false, null));
        _roomId = roomId;
        _memberId = memberId;
        _name = name;
        _getMembers = getMembers;
        _sendFrame = sendFrame;
        _linkUp = linkUp;
        _encrypted = encrypted ?? (() => true);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        _mac = RandomNumberGenerator.GetBytes(6);
        _mac[0] = (byte)((_mac[0] | 0x02) & 0xFE);

        _nextWireId = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8)) >> 1;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _membershipLoop = MembershipLoopAsync();
        _loop = RunAsync();
    }

    public RoomChatSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = _clock();
            var link = _linkUp();
            return new RoomChatSnapshot(
                _roomId,
                _name,
                StateLocked(now, link),
                ParticipantsLocked(now, link),
                _messages.ToArray(),
                link ? _peers.Values.Where(p => p.IsOnline(now) && p.TypingUntil > now).Select(p => p.Name).ToArray() : []);
        }
    }

    public void ReceiveFrame(ReadOnlyMemory<byte> frame)
    {
        var span = frame.Span;
        if (span.Length < Header || !span.Slice(14, 8).SequenceEqual(Magic))
        {
            return;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(23, 2));
        if (length > span.Length - Header)
        {
            return;
        }

        var source = span.Slice(6, 6).ToArray();
        if (source.AsSpan().SequenceEqual(_mac))
        {
            return;
        }

        var type = span[22];
        var payload = span.Slice(Header, length).ToArray();
        List<byte[]>? replies = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var now = _clock();
            switch (type)
            {
                case TypeHello when payload.Length >= 12:
                    var consoleMac = payload.Length >= 19 && payload.AsSpan(12, 6).ContainsAnyExcept((byte)0) ? payload[12..18] : null;
                    var relay = payload.Length >= 19 && (payload[18] & 1) != 0;
                    var consoleName = consoleMac is not null && payload.Length >= 20 && payload[19] is > 0 and <= MaxConsoleName && payload.Length >= 20 + payload[19]
                        ? Encoding.ASCII.GetString(payload, 20, payload[19])
                        : null;
                    OnHelloLocked(Encoding.ASCII.GetString(payload, 0, 12), source, now, consoleMac, relay, consoleName);
                    break;

                case TypeMessage when payload.Length >= 9 && span.Slice(0, 6).SequenceEqual(_mac):
                    replies = OnMessageLocked(source, payload, now);
                    break;

                case TypeAck when payload.Length == 8 && span.Slice(0, 6).SequenceEqual(_mac):
                    OnAckLocked(source, BinaryPrimitives.ReadUInt64BigEndian(payload));
                    break;

                case TypeTyping when span.Slice(0, 6).SequenceEqual(_mac):
                    if (_peers.Values.FirstOrDefault(p => p.Mac.AsSpan().SequenceEqual(source)) is { } typist)
                    {
                        typist.TypingUntil = now + TypingShownFor;
                    }

                    break;
            }
        }

        if (replies is not null)
        {
            _ = SendAllAsync(replies);
        }
    }

    public void Send(string text)
    {
        text = text.Trim();
        if (text.Length is 0 or > MaxMessageLength
            || Encoding.UTF8.GetByteCount(text) > 1280
            || text.Any(c => char.IsControl(c) && c is not '\n' and not '\t'))
        {
            throw new ArgumentException($"Use 1–{MaxMessageLength} characters.");
        }

        List<byte[]> frames;
        lock (_gate)
        {
            var now = _clock();
            var online = _linkUp() ? _peers.Values.Where(p => p.IsOnline(now)).ToList() : [];
            if (online.Count == 0)
            {
                throw new InvalidOperationException("Wait for your friend’s chat to connect.");
            }

            if (now - _lastSend < TimeSpan.FromMilliseconds(400))
            {
                throw new InvalidOperationException("Give that message a moment before sending another.");
            }

            _lastSend = now;
            _lastTyping = DateTimeOffset.MinValue;
            var localId = ++_nextLocalId;
            var wireId = ++_nextWireId;
            var body = Encoding.UTF8.GetBytes(text);
            var payload = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt64BigEndian(payload, wireId);
            body.CopyTo(payload, 8);

            var outgoing = new Outgoing(localId, payload, now, online.ToDictionary(p => p.MemberId, _ => false));
            _pending[wireId] = outgoing;
            AddMessageLocked(new ChatMessage(localId, _name, text, true, now, "Sending"));
            frames = online.Select(p => Frame(p.Mac, TypeMessage, payload)).ToList();
            outgoing.LastSent = now;
        }

        _ = SendAllAsync(frames);
    }

    public void NotifyTyping()
    {
        List<byte[]> frames;
        lock (_gate)
        {
            var now = _clock();
            if (now - _lastTyping < TypingResendAfter || !_linkUp())
            {
                return;
            }

            _lastTyping = now;
            frames = _peers.Values.Where(p => p.IsOnline(now)).Select(p => Frame(p.Mac, TypeTyping, [])).ToList();
        }

        _ = SendAllAsync(frames);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (var task in new[] { _loop, _membershipLoop })
        {
            if (task is not null)
            {
                try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }

        lock (_gate)
        {
            _peers.Clear();
            _pending.Clear();
            _messages.Clear();
        }

        _stop.Dispose();
    }

    private string StateLocked(DateTimeOffset now, bool link)
    {
        if (link && _peers.Values.Any(p => p.IsOnline(now)))
        {
            return _encrypted() ? "Connected · encrypted room tunnel" : "Connected · local link, not encrypted";
        }

        return ParticipantsLocked(now, link).Length > 0 ? "Connecting chat" : "Waiting for friend";
    }

    private ChatParticipant[] ParticipantsLocked(DateTimeOffset now, bool link)
    {
        // A friend counts while the room server lists them or their PC still talks to us: the
        // server can briefly forget a room (restart) while everyone is still here.
        var listed = _members.Where(m => m.MemberId != _memberId).Select(m => (m.MemberId, m.DisplayName));
        var stillTalking = _peers.Values.Where(p => p.IsOnline(now) && _members.All(m => m.MemberId != p.MemberId)).Select(p => (p.MemberId, DisplayName: p.Name));
        return listed.Concat(stillTalking)
            .Select(m => _peers.TryGetValue(m.MemberId, out var peer)
                ? new ChatParticipant(m.MemberId, peer.Name, link && peer.IsOnline(now),
                    peer.ConsoleMac is null ? null : string.Join(":", peer.ConsoleMac.Select(b => b.ToString("X2"))), peer.Relay)
                : new ChatParticipant(m.MemberId, m.DisplayName, false))
            .ToArray();
    }

    private void OnHelloLocked(string memberId, byte[] source, DateTimeOffset now, byte[]? consoleMac, bool relay, string? consoleName)
    {
        if (memberId == _memberId)
        {
            return;
        }

        var member = _members.FirstOrDefault(m => m.MemberId == memberId);
        if (_peers.TryGetValue(memberId, out var peer))
        {
            if (!peer.Mac.AsSpan().SequenceEqual(source))
            {
                return;
            }

            peer.LastHello = now;
            peer.Name = member?.DisplayName ?? peer.Name;
            peer.ConsoleMac = consoleMac;
            peer.ConsoleName = consoleName;
            peer.Relay = relay;
            return;
        }

        if (member is null)
        {
            return;
        }

        if (_peers.Count >= MaxPeers || _peers.Values.Any(p => p.Mac.AsSpan().SequenceEqual(source)))
        {
            return;
        }

        _peers[memberId] = new Peer(memberId, member.DisplayName, source) { LastHello = now, ConsoleMac = consoleMac, ConsoleName = consoleName, Relay = relay };
    }

    private List<byte[]>? OnMessageLocked(byte[] source, byte[] payload, DateTimeOffset now)
    {
        var peer = _peers.Values.FirstOrDefault(p => p.Mac.AsSpan().SequenceEqual(source));
        if (peer is null)
        {
            return null;
        }

        var wireId = BinaryPrimitives.ReadUInt64BigEndian(payload);
        var ack = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(ack, wireId);
        var replies = new List<byte[]> { Frame(peer.Mac, TypeAck, ack) };

        if (!peer.Seen.Add(wireId))
        {
            return replies;
        }

        if (peer.Seen.Count > 512)
        {
            peer.Seen.Clear();
            peer.Seen.Add(wireId);
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(payload, 8, payload.Length - 8);
        }
        catch (DecoderFallbackException)
        {
            return replies;
        }

        if (text.Length is 0 or > MaxMessageLength || text.Any(c => char.IsControl(c) && c is not '\n' and not '\t'))
        {
            return replies;
        }

        peer.TypingUntil = DateTimeOffset.MinValue;
        AddMessageLocked(new ChatMessage(++_nextLocalId, peer.Name, text, false, now, "Received"));
        return replies;
    }

    private void OnAckLocked(byte[] source, ulong wireId)
    {
        var peer = _peers.Values.FirstOrDefault(p => p.Mac.AsSpan().SequenceEqual(source));
        if (peer is null || !_pending.TryGetValue(wireId, out var outgoing) || !outgoing.Acked.ContainsKey(peer.MemberId))
        {
            return;
        }

        outgoing.Acked[peer.MemberId] = true;
        if (outgoing.Acked.Values.All(acked => acked))
        {
            _pending.Remove(wireId);
            SetDeliveryLocked(outgoing.LocalId, "Delivered");
        }
    }

    private async Task RunAsync()
    {
        var nextHello = DateTimeOffset.MinValue;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var frames = new List<byte[]>();
                lock (_gate)
                {
                    var now = _clock();

                    foreach (var gone in _peers.Where(kv => _members.All(m => m.MemberId != kv.Key) && !kv.Value.IsOnline(_clock())).Select(kv => kv.Key).ToList())
                    {
                        _peers.Remove(gone);
                    }

                    if (now >= nextHello)
                    {
                        var hello = HelloPayload();
                        frames.Add(Frame(Broadcast, TypeHello, hello));
                        frames.AddRange(_peers.Values.Select(peer => Frame(peer.Mac, TypeHello, hello)));
                        nextHello = now + HelloInterval;
                    }

                    foreach (var (wireId, outgoing) in _pending.ToList())
                    {
                        if (now - outgoing.Created > GiveUpAfter)
                        {
                            _pending.Remove(wireId);
                            SetDeliveryLocked(outgoing.LocalId, "Unconfirmed");
                            continue;
                        }

                        if (now - outgoing.LastSent < RetryInterval || !_linkUp())
                        {
                            continue;
                        }

                        outgoing.LastSent = now;
                        foreach (var (memberId, acked) in outgoing.Acked)
                        {
                            if (!acked && _peers.TryGetValue(memberId, out var peer))
                            {
                                frames.Add(Frame(peer.Mac, TypeMessage, outgoing.Payload));
                            }
                        }
                    }
                }

                await SendAllAsync(frames).ConfigureAwait(false);
                await Task.Delay(100, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private byte[] HelloPayload()
    {
        var (consoleMac, relay, console) = _self();
        var name = console is null ? [] : Encoding.ASCII.GetBytes(new string(console.Where(c => c is >= ' ' and <= '~').Take(MaxConsoleName).ToArray()));
        var payload = new byte[20 + name.Length];
        Encoding.ASCII.GetBytes(_memberId.PadRight(12)[..12]).CopyTo(payload, 0);
        if (consoleMac is { Length: 6 })
        {
            consoleMac.CopyTo(payload, 12);
        }

        payload[18] = (byte)(relay ? 1 : 0);
        payload[19] = (byte)name.Length;
        name.CopyTo(payload, 20);
        return payload;
    }

    public string? ConsoleNameOf(string memberId)
    {
        lock (_gate)
        {
            return _peers.TryGetValue(memberId, out var peer) ? peer.ConsoleName : null;
        }
    }

    private async Task MembershipLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(4));
                var members = await _getMembers(deadline.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    _members = members.Take(MaxPeers + 1).ToArray();
                    foreach (var gone in _peers.Where(kv => _members.All(m => m.MemberId != kv.Key) && !kv.Value.IsOnline(_clock())).Select(kv => kv.Key).ToList())
                    {
                        _peers.Remove(gone);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch
            {
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendAllAsync(List<byte[]> frames)
    {
        foreach (var frame in frames)
        {
            try
            {
                await _sendFrame(frame, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
            }
        }
    }

    private byte[] Frame(byte[] destination, byte type, byte[] payload)
    {
        var frame = new byte[Math.Max(60, Header + payload.Length)];
        destination.CopyTo(frame, 0);
        _mac.CopyTo(frame, 6);
        frame[12] = 0x88;
        frame[13] = 0xB5;
        Magic.CopyTo(frame.AsSpan(14));
        frame[22] = type;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(23, 2), (ushort)payload.Length);
        payload.CopyTo(frame, Header);
        return frame;
    }

    private void AddMessageLocked(ChatMessage message)
    {
        _messages.Add(message);
        while (_messages.Count > MaxHistory)
        {
            _messages.RemoveAt(0);
        }
    }

    private void SetDeliveryLocked(long localId, string delivery)
    {
        var index = _messages.FindIndex(m => m.Id == localId);
        if (index >= 0)
        {
            _messages[index] = _messages[index] with { Delivery = delivery };
        }
    }

    private sealed class Peer(string memberId, string name, byte[] mac)
    {
        public string MemberId { get; } = memberId;
        public string Name { get; set; } = name;
        public byte[] Mac { get; } = mac;
        public DateTimeOffset LastHello { get; set; }
        public byte[]? ConsoleMac { get; set; }
        public string? ConsoleName { get; set; }
        public bool Relay { get; set; }
        public DateTimeOffset TypingUntil { get; set; }
        public HashSet<ulong> Seen { get; } = [];

        public bool IsOnline(DateTimeOffset now) => now - LastHello <= PeerTimeout;
    }

    private sealed class Outgoing(long localId, byte[] payload, DateTimeOffset created, Dictionary<string, bool> acked)
    {
        public long LocalId { get; } = localId;
        public byte[] Payload { get; } = payload;
        public DateTimeOffset Created { get; } = created;
        public Dictionary<string, bool> Acked { get; } = acked;
        public DateTimeOffset LastSent { get; set; }
    }
}
