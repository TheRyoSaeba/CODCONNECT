namespace CODConnect.Core.Rooms;

public sealed record RoomOptions
{
    public string RoomCode { get; init; } = string.Empty;

    public string SubnetCidr { get; init; } = "10.42.0.0/24";

    public IPv4Address ServerAddress { get; init; } = new(10, 42, 0, 1);

    public IPv4Address GatewayAddress { get; init; } = new(10, 42, 0, 254);

    public IPv4Address PcAddress { get; init; } = new(10, 42, 0, 253);

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromHours(8);

    public IPv4Address SubnetMask
    {
        get
        {
            var prefix = int.Parse(SubnetCidr.Split('/')[1], System.Globalization.CultureInfo.InvariantCulture);
            return IPv4Address.FromUInt32(prefix == 0 ? 0u : uint.MaxValue << (32 - prefix));
        }
    }

    public int MaxPeers { get; init; } = 8;

    public bool StrictConsoleScoping { get; init; } = true;
}
