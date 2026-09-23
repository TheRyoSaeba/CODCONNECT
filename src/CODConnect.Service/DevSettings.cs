using System.Text.Json;

namespace CODConnect.Service;

public sealed record DevSettingsData(bool FriendsConsoleAccess = false, bool RelayOnly = false, string? RoomServer = null);

public sealed class DevSettings
{
    private readonly string _path;
    private readonly string _defaultRoomServer;
    private readonly Func<Uri, CancellationToken, Task<bool>> _probe;
    private readonly object _gate = new();
    private DevSettingsData _data;
    private HttpClient _rendezvous;

    public DevSettings(string defaultRoomServer, string? path = null, Func<Uri, CancellationToken, Task<bool>>? probe = null)
    {
        _defaultRoomServer = Normalize(defaultRoomServer);
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CODCONNECT", "settings.json");
        _probe = probe ?? ProbeAsync;
        _data = Load();
        _rendezvous = NewClient(RoomServer);
    }

    public string DefaultRoomServer => _defaultRoomServer;

    public DevSettingsData Current { get { lock (_gate) return _data; } }

    public bool FriendsConsoleAccess => Current.FriendsConsoleAccess;

    public bool RelayOnly => Current.RelayOnly;

    public string RoomServer => Current.RoomServer ?? _defaultRoomServer;

    public HttpClient Rendezvous { get { lock (_gate) return _rendezvous; } }

    public void SetToggles(bool friendsConsoleAccess, bool relayOnly)
    {
        lock (_gate)
        {
            _data = _data with { FriendsConsoleAccess = friendsConsoleAccess, RelayOnly = relayOnly };
            Save();
        }
    }

    public async Task SetRoomServerAsync(string? url, CancellationToken cancellationToken = default)
    {
        var chosen = string.IsNullOrWhiteSpace(url) ? _defaultRoomServer : Normalize(url);
        if (!Uri.TryCreate(chosen, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && IsLocal(uri))))
        {
            throw new ArgumentException("Enter the server's full address, starting with https://.");
        }

        if (chosen != _defaultRoomServer && !await _probe(uri, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"{uri.Host} did not answer as a CODCONNECT room server. Check the address and that it is running.");
        }

        lock (_gate)
        {
            _data = _data with { RoomServer = chosen == _defaultRoomServer ? null : chosen };
            _rendezvous = NewClient(RoomServer);
            Save();
        }
    }

    private static bool IsLocal(Uri uri)
        => uri.IsLoopback
           || (System.Net.IPAddress.TryParse(uri.Host, out var ip) && ip.GetAddressBytes() is [10, ..] or [192, 168, ..] or [172, >= 16 and <= 31, ..]);

    private static string Normalize(string url) => url.Trim().TrimEnd('/') + "/";

    private static HttpClient NewClient(string url) => new() { BaseAddress = new Uri(url) };

    private static async Task<bool> ProbeAsync(Uri server, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var response = await http.GetAsync(new Uri(server, "healthz"), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private DevSettingsData Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<DevSettingsData>(File.ReadAllText(_path)) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data));
    }
}
