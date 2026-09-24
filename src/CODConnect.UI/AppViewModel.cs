using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;

namespace CODConnect.UI;

public enum Screen
{
    Home,
    Hosting,
    Connecting,
    Connected,
}

public sealed class AppViewModel : INotifyPropertyChanged
{
    private readonly ServiceIpcClient _pipe = new();
    private SessionManager? _embedded;
    private HttpClient? _embeddedRendezvous;
    private CancellationTokenSource? _poll;
    private IpcStatus? _lastStatus;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<IpcAdapterInfo> Adapters { get; } = new();

    public string BackendMode { get; private set; } = "service";

    public Screen Screen
    {
        get;
        private set
        {
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsHome));
            OnPropertyChanged(nameof(IsHosting));
            OnPropertyChanged(nameof(IsConnecting));
            OnPropertyChanged(nameof(IsConnected));
        }
    } = Screen.Home;

    public bool IsHome => Screen == Screen.Home;
    public bool IsHosting => Screen == Screen.Hosting;
    public bool IsConnecting => Screen == Screen.Connecting;
    public bool IsConnected => Screen == Screen.Connected;

    public string DisplayName { get; set; } = "Player";
    public RoomChatSnapshot? Chat { get; private set; }
    public string ChatError { get; private set; } = "";
    public bool ChatSending { get; private set; }

    public event Action<ChatMessage>? IncomingChat;
    private string _chatSeenRoom = "";
    private long _chatSeenId;

    private void SetChat(RoomChatSnapshot? next)
    {
        Chat = next;
        if (next is null) { _chatSeenRoom = ""; _chatSeenId = 0; return; }
        var newest = next.Messages.Count == 0 ? 0 : next.Messages.Max(m => m.Id);
        if (next.RoomId != _chatSeenRoom)
        {
            _chatSeenRoom = next.RoomId; _chatSeenId = newest;
            return;
        }

        foreach (var message in next.Messages.Where(m => m.Id > _chatSeenId && !m.Own))
        {
            IncomingChat?.Invoke(message);
        }
        _chatSeenId = Math.Max(_chatSeenId, newest);
    }

    public async Task<bool> SendChatAsync(string message)
    {
        if (ChatSending) return false;
        ChatSending = true; ChatError = ""; OnPropertyChanged(nameof(Chat));
        try
        {
            if (BackendMode == "service")
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var result = await _pipe.SendAsync(new IpcRequest("chat-send", Message: message), timeout.Token);
                if (!result.Ok) throw new InvalidOperationException(result.Error ?? "Couldn’t send the message.");
                SetChat(result.Status?.Chat);
            }
            else { _embedded!.SendChat(message); SetChat(_embedded.Chat); }
            return true;
        }
        catch (Exception ex) { ChatError = ex is InvalidOperationException or ArgumentException ? ex.Message : "Couldn’t reach chat. Check the connection and try again."; return false; }
        finally { ChatSending = false; OnPropertyChanged(nameof(Chat)); }
    }

    public string RoomCode { get; private set; } = string.Empty;

    public bool JoinMode { get; private set; }

    public string AdapterSelection { get; set; } = string.Empty;

    public bool WifiMode { get; set; }

    public IpcWifiCapability? WifiCapability { get; private set; }

    public IpcWifiNetwork? WifiNetwork { get; private set; }

    public async Task RefreshWifiCapabilityAsync()
    {
        try
        {
            WifiCapability = BackendMode == "service"
                ? (await _pipe.SendAsync(new IpcRequest("wifi-capability")).ConfigureAwait(true)).WifiCapability
                : await _embedded!.CheckWifiAsync().ConfigureAwait(true);
        }
        catch
        {
            WifiCapability = new IpcWifiCapability(false, "Couldn't ask the background service about Wi-Fi mode.", null, null, null, []);
        }

        OnPropertyChanged(nameof(WifiCapability));
    }

    public Func<string, bool>? ConfirmInternetAdapter { get; set; }

    private bool _allowInternetAdapter;

    public string StatusText { get; private set; } = string.Empty;

    public string CountersText { get; private set; } = string.Empty;

    public string ErrorText { get; private set; } = string.Empty;

    public string ProgressText { get; private set; } = string.Empty;

    public NetworkVisualState Visual { get; private set; } = NetworkVisualState.FromScreen(Screen.Home);

    public string? LocalConsoleIdentity { get; private set; }

    public string? RemoteConsoleIdentity { get; private set; }

    public string? ConnectionKind { get; private set; }

    public string? ConsoleInternet { get; private set; }

    public string? DevError { get; private set; }

    internal IpcDevSettings? DevSettingsFixture { get; set; }

    public async Task<IpcDevSettings?> LoadDevSettingsAsync()
    {
        DevError = null;
        if (DevSettingsFixture is { } fixture)
        {
            return fixture;
        }

        if (BackendMode != "service")
        {
            DevError = "Developer options need the CODCONNECT service.";
            return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await _pipe.SendAsync(new IpcRequest("dev-settings"), timeout.Token);
            DevError = response.Ok ? null : response.Error;
            return response.DevSettings;
        }
        catch (Exception ex) when (ex is System.IO.IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            DevError = "The CODCONNECT service is not answering.";
            return null;
        }
    }

    public async Task<string?> SaveDevSettingsAsync(IpcDevSettings settings)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            var response = await _pipe.SendAsync(new IpcRequest("set-dev-settings", DevSettings: settings), timeout.Token);
            return response.Ok ? null : response.Error ?? "Couldn’t save.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return "The CODCONNECT service is not answering.";
        }
    }

    public IReadOnlyList<IpcPlayer> Players { get; private set; } = [];

    public string? RoomEvent => DateTimeOffset.UtcNow < _roomEventUntil ? _roomEvent : null;

    private string? _roomEvent;
    private DateTimeOffset _roomEventUntil;
    private IReadOnlyCollection<string> _players = [];
    private readonly HashSet<string> _everConnected = [];

    public bool IsBusy { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            var response = await _pipe.SendAsync(new IpcRequest("ping"));
            if (response.Ok)
            {
                BackendMode = "service";
                await LoadAdaptersAsync().ConfigureAwait(true);
                await RefreshWifiCapabilityAsync().ConfigureAwait(true);
                await ReattachAsync().ConfigureAwait(true);
                StartPoll();
                return;
            }
        }
        catch
        {
        }

        UseEmbeddedBackend();
        await LoadAdaptersAsync().ConfigureAwait(true);
        await RefreshWifiCapabilityAsync().ConfigureAwait(true);
        StartPoll();
    }

    public async Task CreateRoomAsync()
    {
        if (IsBusy || !ValidateInput()) return;
        ErrorText = string.Empty;
        IsBusy = true;
        JoinMode = false;
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(IsBusy));
        try
        {
            if (!ConfirmAdapterIfNeeded())
            {
                return;
            }

            if (BackendMode == "service")
            {
                var response = await SendWithProgressAsync(new IpcRequest("host", DisplayName, Adapter: AdapterSelection, AllowInternetAdapter: _allowInternetAdapter, Mode: WifiMode ? "wifi" : null));
                if (!response.Ok)
                {
                    ErrorText = response.Error ?? "Hosting failed.";
                    return;
                }

                RoomCode = response.RoomCode ?? string.Empty;
            }
            else
            {
                RoomCode = await _embedded!.StartHostAsync(DisplayName, NullIfAuto(AdapterSelection), _allowInternetAdapter, WifiMode);
            }

            Screen = Screen.Hosting;
        }
        catch (Exception ex)
        {
            ErrorText = DescribeError(ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(ErrorText));
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    private async Task<IpcResponse> SendWithProgressAsync(IpcRequest request)
    {
        SetProgress("Getting ready…");
        using var done = new CancellationTokenSource();
        var watch = WatchProgressAsync(done.Token);
        try
        {
            return await _pipe.SendAsync(request).ConfigureAwait(true);
        }
        finally
        {
            done.Cancel();
            await watch.ConfigureAwait(true);
            SetProgress(string.Empty);
        }
    }

    private async Task WatchProgressAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, token).ConfigureAwait(true);
                var progress = (await _pipe.SendAsync(new IpcRequest("status"), token).ConfigureAwait(true)).Status?.Progress;
                if (!string.IsNullOrEmpty(progress) && !token.IsCancellationRequested)
                {
                    SetProgress(progress);
                }
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

    private void SetProgress(string text)
    {
        if (text == ProgressText) return;
        ProgressText = text;
        OnPropertyChanged(nameof(ProgressText));
    }

    public async Task JoinRoomAsync(string code)
    {
        if (IsBusy || !ValidateInput(code)) return;
        ErrorText = string.Empty;
        IsBusy = true;
        JoinMode = true;
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(IsBusy));
        try
        {
            if (!ConfirmAdapterIfNeeded())
            {
                return;
            }

            if (BackendMode == "service")
            {
                var response = await SendWithProgressAsync(new IpcRequest("join", DisplayName, RoomCode: code, Adapter: AdapterSelection, AllowInternetAdapter: _allowInternetAdapter, Mode: WifiMode ? "wifi" : null));
                if (!response.Ok)
                {
                    ErrorText = response.Error ?? "Joining failed.";
                    return;
                }
            }
            else
            {
                await _embedded!.StartJoinAsync(code, DisplayName, NullIfAuto(AdapterSelection), _allowInternetAdapter, WifiMode);
            }

            RoomCode = code;
            Screen = Screen.Connecting;
        }
        catch (Exception ex)
        {
            ErrorText = DescribeError(ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(ErrorText));
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    public async Task DisconnectAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorText = string.Empty;
        OnPropertyChanged(nameof(IsBusy));
        try
        {
            if (BackendMode == "service")
            {
                var response = await _pipe.SendAsync(new IpcRequest("stop"));
                if (!response.Ok) throw new InvalidOperationException(response.Error ?? "Could not leave the room.");
            }
            else if (_embedded is not null)
            {
                await _embedded.StopAsync();
            }
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            OnPropertyChanged(nameof(ErrorText));
            return;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsBusy));
        }

        RoomCode = string.Empty;
        StatusText = string.Empty;
        CountersText = string.Empty;
        ResetReadiness();
        Screen = Screen.Home;
        OnPropertyChanged(nameof(RoomCode));
    }

    private async Task ReattachAsync()
    {
        try
        {
            var status = (await _pipe.SendAsync(new IpcRequest("status")).ConfigureAwait(true)).Status;
            if (status?.HasSession != true)
            {
                return;
            }

            _lastStatus = status;
            RoomCode = status.RoomCode ?? string.Empty;
            JoinMode = status.Role == "Joiner";
            Screen = status.TunnelState == "Connected" ? Screen.Connected : JoinMode ? Screen.Connecting : Screen.Hosting;
            OnPropertyChanged(nameof(RoomCode));
        }
        catch
        {
        }
    }

    private void ResetReadiness()
    {
        Chat = null; ChatError = ""; OnPropertyChanged(nameof(Chat));
        Visual = NetworkVisualState.FromScreen(Screen.Home);
        LocalConsoleIdentity = null;
        RemoteConsoleIdentity = null;
        ConnectionKind = null;
        ConsoleInternet = null;
        Players = [];
        _players = [];
        _everConnected.Clear();
        _roomEvent = null;
        OnPropertyChanged(nameof(Visual));
    }

    private void UseEmbeddedBackend()
    {
        BackendMode = "embedded";
        _embeddedRendezvous = new HttpClient { BaseAddress = new Uri(GetRendezvousUrl()) };
        _embedded = new SessionManager(
            ConsoleAdapterFactory.OpenAsync,
            new AutoRoomTransport(),
            _embeddedRendezvous,
            log: _ => { });
        OnPropertyChanged(nameof(BackendMode));
    }

    private async Task LoadAdaptersAsync()
    {
        try
        {
            IReadOnlyList<IpcAdapterInfo> adapters;
            if (BackendMode == "service")
            {
                var response = await _pipe.SendAsync(new IpcRequest("list-adapters"));
                adapters = response.Adapters ?? [];
            }
            else
            {
                adapters = ConsoleAdapterFactory.ListAdapters();
            }

            Adapters.Clear();
            foreach (var adapter in adapters)
            {
                Adapters.Add(adapter);
            }
        }
        catch
        {
        }
    }

    private void StartPoll()
    {
        _poll = new CancellationTokenSource();
        _ = PollLoopAsync(_poll.Token);
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, token).ConfigureAwait(true);
                await PollOnceAsync(token).ConfigureAwait(true);
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

    private async Task PollOnceAsync(CancellationToken token)
    {
        if (IsBusy) return;
        if (BackendMode == "service")
        {
            var response = await _pipe.SendAsync(new IpcRequest("status"), token);
            _lastStatus = response.Status;
        }

        var hasSession = BackendMode == "service" ? _lastStatus?.HasSession == true : _embedded!.HasSession;
        var role = BackendMode == "service" ? _lastStatus?.Role : _embedded!.Role;
        var tunnel = BackendMode == "service" ? _lastStatus?.TunnelState : _embedded!.TunnelState;
        var counters = BackendMode == "service" ? _lastStatus?.Counters : _embedded!.Counters is { } snapshot ? ToDto(snapshot) : null;
        var leases = BackendMode == "service" ? _lastStatus?.Leases ?? [] : _embedded!.Leases;
        var devices = BackendMode == "service" ? _lastStatus?.Devices ?? [] : _embedded!.Devices;
        var localReady = BackendMode == "service" ? _lastStatus?.LocalConsoleReady == true : _embedded!.LocalConsoleReady;
        var remoteReady = BackendMode == "service" ? _lastStatus?.RemoteConsoleReady == true : _embedded!.RemoteConsoleReady;
        var connectionKind = BackendMode == "service" ? _lastStatus?.ConnectionKind : _embedded!.ConnectionKind;
        var wifiNetwork = BackendMode == "service" ? _lastStatus?.WifiNetwork : _embedded!.WifiNetwork;
        if (!Equals(wifiNetwork, WifiNetwork))
        {
            WifiNetwork = wifiNetwork;
            OnPropertyChanged(nameof(WifiNetwork));
        }
        SetChat(hasSession ? BackendMode == "service" ? _lastStatus?.Chat : _embedded!.Chat : null);
        OnPropertyChanged(nameof(Chat));

        if (Screen is Screen.Hosting or Screen.Connecting or Screen.Connected)
        {
            if (!hasSession)
            {
                RoomCode = string.Empty;
                StatusText = string.Empty;
                CountersText = string.Empty;
                ResetReadiness();
                var reason = BackendMode == "service" ? _lastStatus?.LastEndReason : _embedded!.LastEndReason;
                if (!string.IsNullOrEmpty(reason))
                {
                    ErrorText = reason;
                    OnPropertyChanged(nameof(ErrorText));
                }

                Screen = Screen.Home;
                OnPropertyChanged(nameof(RoomCode));
                return;
            }

            var tunnelUp = tunnel == "Connected";
            Players = (BackendMode == "service" ? _lastStatus?.Players : _embedded!.Players) ?? [];
            var arrived = Players.Where(p => p.Connected && _everConnected.Add(p.Name)).Select(p => p.Name).ToArray();
            var listed = Players.Select(p => p.Name).ToArray();
            var change = arrived.Length > 0 ? RoomBadge.Change([], arrived) : RoomBadge.Change(_players.Where(_everConnected.Contains).ToArray(), listed);
            if (change is not null)
            {
                _roomEvent = change;
                _roomEventUntil = DateTimeOffset.UtcNow.AddSeconds(4);
            }

            _players = listed;
            Visual = new NetworkVisualState(
                LocalReady: localReady,
                RemoteReady: remoteReady && tunnelUp,
                TunnelConnected: tunnelUp,
                LocalAttached: true,
                RemoteAttached: tunnelUp,
                Friends: Players.Select(p => new FriendVisual(p.Name, p.Connected, p.ConsoleReady, RoomBadge.PlayerDetail(p))).ToArray());
            LocalConsoleIdentity = DescribeSide(devices, "Local");
            RemoteConsoleIdentity = tunnelUp ? DescribeSide(devices, "Remote") : null;
            ConnectionKind = tunnelUp ? connectionKind : null;
            ConsoleInternet = BackendMode == "service" ? _lastStatus?.ConsoleInternet : _embedded!.ConsoleInternet;
            OnPropertyChanged(nameof(Visual));

            if (tunnel == "Connected" && Screen != Screen.Connected)
            {
                Screen = Screen.Connected;
            }
            else if (tunnel != "Connected" && Screen == Screen.Connected)
            {
                Screen = Screen.Connecting;
            }

            StatusText = $"{role} · tunnel {tunnel} · {leases.Count} lease(s)";
            CountersText = counters is null
                ? string.Empty
                : $"in {counters.FramesCaptured} · out {counters.FramesInjected} · bc {counters.BroadcastFramesIn}/{counters.BroadcastFramesOut} · drop {counters.FramesDropped} · {counters.BytesIn >> 10} KiB/{counters.BytesOut >> 10} KiB";
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CountersText));
        }
    }

    private static string? DescribeSide(IReadOnlyList<IpcDevice> devices, string side)
    {
        var onSide = devices.Where(d => d.Side == side).ToList();
        if (onSide.Count == 0) return null;
        return onSide.FirstOrDefault(d => d.Identity is not null)?.Identity ?? "Unknown device";
    }

    private static NetworkCounterSnapshotDto? ToDto(NetworkCounterSnapshot snapshot)
        => new(snapshot.FramesCaptured, snapshot.FramesInjected, snapshot.BroadcastFramesIn, snapshot.BroadcastFramesOut,
               snapshot.UnicastFramesIn, snapshot.UnicastFramesOut, snapshot.FramesDropped, snapshot.MalformedFrames,
               snapshot.BytesIn, snapshot.BytesOut);

    private bool ConfirmAdapterIfNeeded()
    {
        _allowInternetAdapter = false;
        if (WifiMode || string.IsNullOrWhiteSpace(AdapterSelection))
        {
            return true;
        }

        var selected = Adapters.FirstOrDefault(a => a.Index.ToString() == AdapterSelection);
        if (selected?.CarriesInternet != true)
        {
            return true;
        }

        if (ConfirmInternetAdapter?.Invoke(selected.Name) != true)
        {
            ErrorText = $"'{selected.Name}' carries this PC's Internet. Choose the spare port your console is plugged into.";
            OnPropertyChanged(nameof(ErrorText));
            return false;
        }

        _allowInternetAdapter = true;
        return true;
    }

    private static string? NullIfAuto(string? adapter)
        => string.IsNullOrWhiteSpace(adapter) || adapter.StartsWith("Auto", StringComparison.OrdinalIgnoreCase) ? null : adapter;

    private static string DescribeError(Exception ex)
    {
        var walk = ex;
        while (walk is not null)
        {
            if (walk is System.Net.Sockets.SocketException or HttpRequestException)
            {
                return "Cannot reach the room server. Run the rendezvous server locally "
                    + "(dotnet run --project server/CODConnect.Rendezvous) or point the "
                    + "CODCONNECT_RENDEZVOUS_URL environment variable at a reachable server.";
            }

            walk = walk.InnerException;
        }

        return ex.Message;
    }

    public void StopPolling() => _poll?.Cancel();

    private bool ValidateInput(string? code = null)
    {
        ErrorText = string.IsNullOrWhiteSpace(DisplayName)
            ? "Enter your name to continue."
            : code is not null && (code.Length != 7 || code[4] != '-' || !code.Where((_, i) => i != 4).All(char.IsAsciiLetterOrDigit))
                ? "Enter a room code in the format K7M4-P2."
                : string.Empty;
        OnPropertyChanged(nameof(ErrorText));
        return ErrorText.Length == 0;
    }

    private static string GetRendezvousUrl()
    {
        var configured = Environment.GetEnvironmentVariable("CODCONNECT_RENDEZVOUS_URL");
        return string.IsNullOrWhiteSpace(configured) ? "http://127.0.0.1:5090/" : configured;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
