namespace CODConnect.PacketEngine;

public sealed record SwitchOptions
{
    public TimeSpan MacAgeingTime { get; init; } = TimeSpan.FromSeconds(300);

    public bool StrictConsoleScoping { get; init; } = true;

    public IReadOnlySet<MacAddress> LocalMacs { get; init; } = new HashSet<MacAddress>();

    public IReadOnlySet<int> LocalOuis { get; init; } = new HashSet<int> { 0x00155D };

    public bool IsLocalSource(MacAddress mac)
        => LocalMacs.Contains(mac) || LocalOuis.Contains(Oui(mac));

    private static int Oui(MacAddress mac)
    {
        var bytes = mac.ToArray();
        return (bytes[0] << 16) | (bytes[1] << 8) | bytes[2];
    }

    public int MaxLearnedMacs { get; init; } = 64;

    public Func<ReadOnlyMemory<byte>, bool>? TunnelIngressFilter { get; init; }
}
