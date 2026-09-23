using CODConnect.SoftEther;

namespace CODConnect.Sessions;

public sealed class AutoRoomTransport : IRoomTransport, IConsoleInternetProvider
{
    private readonly Action<string>? _log;
    private readonly string _mode;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IRoomTransport? _resolved;

    private readonly bool _sweepOrphans;

    public AutoRoomTransport(Action<string>? log = null, string? mode = null, bool sweepOrphans = false)
    {
        _log = log;
        _sweepOrphans = sweepOrphans;
        _mode = (mode ?? Environment.GetEnvironmentVariable("CODCONNECT_TRANSPORT") ?? "auto").Trim().ToLowerInvariant();
    }

    public string? ActiveTransport => _resolved switch
    {
        SoftEtherRoomTransport => "SoftEther",
        TcpRoomTransport => "TCP",
        null => null,
        _ => _resolved.GetType().Name,
    };

    public string ConnectionKind => _resolved?.ConnectionKind ?? "Direct";

    public async Task<IRoomTransport> ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (_resolved is not null)
        {
            return _resolved;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _resolved ??= await SelectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(IConsoleNetworkInterface Port, EndpointInfo Advertise)> HostAsync(CancellationToken cancellationToken = default)
        => await (await ResolveAsync(cancellationToken).ConfigureAwait(false)).HostAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IConsoleNetworkInterface> JoinAsync(IReadOnlyList<EndpointInfo> peers, CancellationToken cancellationToken = default)
        => await (await ResolveAsync(cancellationToken).ConfigureAwait(false)).JoinAsync(peers, cancellationToken).ConfigureAwait(false);

    public async Task<IConsoleNetworkInterface> StartConsoleInternetAsync(IPv4Address gateway, IPv4Address mask, CancellationToken cancellationToken = default)
        => await ResolveAsync(cancellationToken).ConfigureAwait(false) is IConsoleInternetProvider provider
            ? await provider.StartConsoleInternetAsync(gateway, mask, cancellationToken).ConfigureAwait(false)
            : throw new NotSupportedException("Console Internet needs the SoftEther transport.");

    private async Task SweepOrphanedHubsAsync(string adminPassword, CancellationToken cancellationToken)
    {
        try
        {
            var admin = SoftEtherJsonRpcClient.Create(SoftEtherServerSetup.DefaultServerUrl, adminPassword);
            foreach (var hub in await admin.ListHubsAsync(adminPassword, cancellationToken).ConfigureAwait(false))
            {
                if (SoftEtherRoomTransport.IsRoomHubName(hub.Name))
                {
                    await admin.DeleteHubAsync(hub.Name, adminPassword, cancellationToken).ConfigureAwait(false);
                    _log?.Invoke($"removed orphaned room hub {hub.Name}");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"orphaned hub sweep skipped: {ex.Message}");
        }
    }

    private async Task<IRoomTransport> SelectAsync(CancellationToken cancellationToken)
    {
        if (_mode == "tcp")
        {
            _log?.Invoke("transport: direct TCP (CODCONNECT_TRANSPORT=tcp)");
            return new TcpRoomTransport();
        }

        var missing = new List<string>();
        if (!SoftEtherServerSetup.IsServerInstalled) missing.Add("SoftEther VPN Server");
        if (!VpncmdClientControl.IsInstalled) missing.Add("SoftEther VPN Client");

        string? adminPassword = null;
        if (missing.Count == 0)
        {
            adminPassword = await SoftEtherServerSetup.EnsureAdminPasswordAsync(log: _log, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (adminPassword is null)
            {
                missing.Add("SoftEther VPN Server admin access (server not answering, or its admin password was changed by hand)");
            }
        }

        if (missing.Count > 0)
        {
            var reason = "SoftEther transport unavailable: " + string.Join(", ", missing);
            if (_mode == "softether")
            {
                throw new InvalidOperationException(reason);
            }

            _log?.Invoke(reason + "; falling back to direct TCP (same LAN or port-forward only)");
            return new TcpRoomTransport();
        }

        _log?.Invoke("transport: SoftEther (encrypted, NAT-traversing)");
        var transport = new SoftEtherRoomTransport(new SoftEtherTransportOptions
        {
            ServerBaseUrl = SoftEtherServerSetup.DefaultServerUrl,
            AdminPassword = adminPassword!,
            Log = _log,
        });

        if (_sweepOrphans)
        {
            await transport.ClearOwnSettingsAsync(cancellationToken).ConfigureAwait(false);
            await SweepOrphanedHubsAsync(adminPassword!, cancellationToken).ConfigureAwait(false);
        }

        return transport;
    }
}
