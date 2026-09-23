using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CODConnect.SoftEther;

public sealed record SoftEtherHub(string Name, bool Online, int SessionCount);

public interface ISoftEtherAdmin
{
    Task<IReadOnlyList<SoftEtherHub>> ListHubsAsync(string adminPassword, CancellationToken cancellationToken = default);

    Task CreateHubAsync(string name, string adminPassword, CancellationToken cancellationToken = default);

    Task DeleteHubAsync(string name, string adminPassword, CancellationToken cancellationToken = default);

    Task CreateUserAsync(string hubName, string userName, string password, string adminPassword, CancellationToken cancellationToken = default, bool unlimitedBroadcasts = false);

    Task<string?> GetDdnsFqdnAsync(string adminPassword, CancellationToken cancellationToken = default);

    Task<string?> EnableAzureRelayAsync(string adminPassword, CancellationToken cancellationToken = default);

    Task SetServerPasswordAsync(string newPassword, string currentPassword, CancellationToken cancellationToken = default);

    Task<bool> TestAsync(string adminPassword, CancellationToken cancellationToken = default);

    Task EnableSecureNatAsync(string hubName, IPAddress address, IPAddress mask, byte[] mac, string adminPassword, CancellationToken cancellationToken = default);
}

public sealed class SoftEtherRpcException : Exception
{
    public SoftEtherRpcException(long code, string message)
        : base($"SoftEther RPC error {code}: {message}")
    {
        Code = code;
    }

    public long Code { get; }
}

public sealed class SoftEtherJsonRpcClient : ISoftEtherAdmin
{
    private static readonly string[] KnownSuffixes = ["_str", "_u32", "_u64", "_bool", "_bin", "_dt", "_ip", "_uni"];

    private readonly HttpClient _http;

    public SoftEtherJsonRpcClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public static SoftEtherJsonRpcClient Create(Uri serverBaseUrl, string adminPassword, bool acceptAnyCertificate = true)
    {
        var handler = new HttpClientHandler();
        if (acceptAnyCertificate)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        var http = new HttpClient(handler) { BaseAddress = serverBaseUrl, Timeout = TimeSpan.FromSeconds(15) };
        return new SoftEtherJsonRpcClient(http) { AdminPassword = adminPassword };
    }

    private string AdminPassword { get; init; } = string.Empty;

    public async Task<IReadOnlyList<SoftEtherHub>> ListHubsAsync(string adminPassword, CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync("EnumHub", [], adminPassword, cancellationToken).ConfigureAwait(false);
        var hubs = new List<SoftEtherHub>();
        if (TryGetProperty(result, "HubList", out var hubList) && hubList.ValueKind == JsonValueKind.Array)
        {
            foreach (var hub in hubList.EnumerateArray())
            {
                hubs.Add(new SoftEtherHub(
                    GetString(hub, "HubName") ?? string.Empty,
                    GetBool(hub, "Online") ?? false,
                    GetInt(hub, "NumSessions") ?? 0));
            }
        }

        return hubs;
    }

    public async Task CreateHubAsync(string name, string adminPassword, CancellationToken cancellationToken = default)
    {
        await InvokeAsync("CreateHub", new Dictionary<string, object?>
        {
            ["HubName"] = name,
            ["AdminPasswordPlainText"] = adminPassword,
            ["Online"] = true,
        }, adminPassword, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteHubAsync(string name, string adminPassword, CancellationToken cancellationToken = default)
    {
        await InvokeAsync("DeleteHub", new Dictionary<string, object?>
        {
            ["HubName"] = name,
        }, adminPassword, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateUserAsync(string hubName, string userName, string password, string adminPassword, CancellationToken cancellationToken = default, bool unlimitedBroadcasts = false)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["HubName"] = hubName,
            ["Name"] = userName,
            ["AuthType"] = 1,
            ["Auth_Password"] = password,
            ["Realname"] = "CODCONNECT room member",
        };
        if (unlimitedBroadcasts)
        {
            parameters["UsePolicy"] = true;
            parameters["policy:Access"] = true;
            parameters["policy:NoBroadcastLimiter"] = true;
            parameters["policy:MaxConnection"] = 32;
        }

        await InvokeAsync("CreateUser", parameters, adminPassword, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnableSecureNatAsync(string hubName, IPAddress address, IPAddress mask, byte[] mac, string adminPassword, CancellationToken cancellationToken = default)
    {
        await InvokeAsync("SetSecureNATOption", new Dictionary<string, object?>
        {
            ["RpcHubName"] = hubName,
            ["MacAddress"] = mac,
            ["Ip"] = address,
            ["Mask"] = mask,
            ["UseNat"] = true,
            ["Mtu"] = 1500,
            ["NatTcpTimeout"] = 1800,
            ["NatUdpTimeout"] = 60,
            ["UseDhcp"] = false,
            ["DhcpLeaseIPStart"] = address,
            ["DhcpLeaseIPEnd"] = address,
            ["DhcpSubnetMask"] = mask,
            ["DhcpExpireTimeSpan"] = 7200,
            ["DhcpGatewayAddress"] = address,
            ["DhcpDnsServerAddress"] = address,
            ["DhcpDnsServerAddress2"] = IPAddress.Any,
            ["DhcpDomainName"] = "",
            ["SaveLog"] = false,
            ["ApplyDhcpPushRoutes"] = false,
            ["DhcpPushRoutes"] = "",
        }, adminPassword, cancellationToken).ConfigureAwait(false);
        await InvokeAsync("EnableSecureNAT", new Dictionary<string, object?> { ["HubName"] = hubName }, adminPassword, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetDdnsFqdnAsync(string adminPassword, CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync("GetDDnsClientStatus", [], adminPassword, cancellationToken).ConfigureAwait(false);
        var fqdn = GetString(result, "CurrentFqdn");
        return string.IsNullOrWhiteSpace(fqdn) ? null : fqdn;
    }

    public async Task<string?> EnableAzureRelayAsync(string adminPassword, CancellationToken cancellationToken = default)
    {
        var ddns = await InvokeAsync("GetDDnsClientStatus", [], adminPassword, cancellationToken).ConfigureAwait(false);
        var hostName = GetString(ddns, "CurrentHostName");
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return null;
        }

        var status = await InvokeAsync("GetAzureStatus", [], adminPassword, cancellationToken).ConfigureAwait(false);
        if (GetBool(status, "IsEnabled") != true)
        {
            await InvokeAsync("SetAzureStatus", new Dictionary<string, object?> { ["IsEnabled"] = true }, adminPassword, cancellationToken).ConfigureAwait(false);
        }

        return hostName + ".vpnazure.net";
    }

    public async Task SetServerPasswordAsync(string newPassword, string currentPassword, CancellationToken cancellationToken = default)
    {
        await InvokeAsync("SetServerPassword", new Dictionary<string, object?>
        {
            ["PlainTextPassword"] = newPassword,
        }, currentPassword, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TestAsync(string adminPassword, CancellationToken cancellationToken = default)
    {
        try
        {
            await InvokeAsync("EnumHub", [], adminPassword, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SoftEtherRpcException)
        {
            return false;
        }
    }

    public async Task<JsonElement> InvokeAsync(
        string method, Dictionary<string, object?> parameters, string adminPassword, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + adminPassword)));
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "1",
            ["method"] = method,
            ["params"] = SuffixParams(parameters),
        });
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SoftEtherRpcException(401, "administrative authentication failed (wrong admin password?)");
        }

        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt64(out var parsedCode) ? parsedCode : 0;
            var message = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? "unknown error" : "unknown error";
            throw new SoftEtherRpcException(code, message);
        }

        return root.GetProperty("result").Clone();
    }

    private static Dictionary<string, object?> SuffixParams(Dictionary<string, object?> parameters)
        => parameters.ToDictionary(kv => kv.Key + SuffixFor(kv.Value), kv => kv.Value is IPAddress ip ? ip.ToString() : kv.Value);

    private static string SuffixFor(object? value) => value switch
    {
        string => "_str",
        bool => "_bool",
        int or uint => "_u32",
        long or ulong => "_u64",
        byte[] => "_bin",
        IPAddress => "_ip",
        _ => "_str",
    };

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (Matches(property.Name, name))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool Matches(string propertyName, string logicalName)
        => propertyName.Equals(logicalName, StringComparison.OrdinalIgnoreCase)
           || (propertyName.StartsWith(logicalName, StringComparison.OrdinalIgnoreCase)
               && KnownSuffixes.Any(suffix => propertyName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));

    private static string? GetString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? GetInt(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : null;
}
