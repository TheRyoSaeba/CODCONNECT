namespace CODConnect.Sessions;

public interface IRoomTransport
{
    Task<(IConsoleNetworkInterface Port, EndpointInfo Advertise)> HostAsync(CancellationToken cancellationToken = default);

    Task<IConsoleNetworkInterface> JoinAsync(IReadOnlyList<EndpointInfo> peers, CancellationToken cancellationToken = default);

    string ConnectionKind { get; }
}

public interface IConsoleInternetProvider
{
    Task<IConsoleNetworkInterface> StartConsoleInternetAsync(IPv4Address gateway, IPv4Address mask, CancellationToken cancellationToken = default);
}
