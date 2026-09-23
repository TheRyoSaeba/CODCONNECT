namespace CODConnect.Protocol;

public sealed record ChatParticipant(string MemberId, string Name, bool Connected, string? ConsoleMac = null, bool Relay = false);
public sealed record ChatMessage(long Id, string Author, string Text, bool Own, DateTimeOffset Time, string Delivery);
public sealed record RoomChatSnapshot(string RoomId, string SelfName, string State, IReadOnlyList<ChatParticipant> Peers, IReadOnlyList<ChatMessage> Messages);
