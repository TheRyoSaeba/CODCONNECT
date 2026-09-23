using System.Net.Http.Json;

namespace CODConnect.Protocol;

public sealed class RendezvousClient
{
    private readonly HttpClient _http;

    public RendezvousClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<CreateRoomResponse?> CreateRoomAsync(EndpointInfo endpoint, string displayName, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("/v1/rooms", new CreateRoomRequest(displayName, endpoint), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            throw new InvalidOperationException("The rendezvous server rejected the room creation request.");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("Rate limited by the rendezvous server; retry shortly.");
        }

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<CreateRoomResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<JoinResponse?> JoinRoomAsync(string code, string displayName, EndpointInfo? endpoint = null, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync($"/v1/rooms/{Uri.EscapeDataString(code)}/join", new JoinRequest(displayName, endpoint), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("Rate limited by the rendezvous server; retry shortly.");
        }

        if (response.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.BadRequest)
        {
            var reason = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                reason.Contains("name taken", StringComparison.OrdinalIgnoreCase) ? $"Someone in that room is already called {displayName}. Choose another name."
                : reason.Contains("room full", StringComparison.OrdinalIgnoreCase) ? "That room is full."
                : "Use a name of 1-12 ordinary characters.");
        }

        if (!response.IsSuccessStatusCode && response.StatusCode is not System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"Joining room {code} failed with HTTP {(int)response.StatusCode}.");
        }

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<JoinResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<RoomInfo?> GetRoomAsync(string code, string memberId, string memberSecret, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/rooms/{Uri.EscapeDataString(code)}");
        request.Headers.Add("X-Member-Id", memberId);
        request.Headers.Add("X-Member-Secret", memberSecret);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<RoomInfo>(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        throw new InvalidOperationException($"The room server returned HTTP {(int)response.StatusCode} for room {code}.");
    }

    public async Task<bool> CloseRoomAsync(string code, string adminKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/v1/rooms/{Uri.EscapeDataString(code)}");
        request.Headers.TryAddWithoutValidation("X-Admin-Key", adminKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.StatusCode == System.Net.HttpStatusCode.NoContent;
    }

    public async Task<HeartbeatResult> HeartbeatAsync(string code, string memberId, string memberSecret, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"/v1/rooms/{Uri.EscapeDataString(code)}/members/{Uri.EscapeDataString(memberId)}/heartbeat",
            new HeartbeatRequest(memberSecret), cancellationToken).ConfigureAwait(false);
        switch (response.StatusCode)
        {
            case System.Net.HttpStatusCode.NoContent:
                return HeartbeatResult.Alive;
            case System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.Forbidden:
                return HeartbeatResult.MemberGone;
            case System.Net.HttpStatusCode.NotFound:
                return await ErrorAsync(response, cancellationToken).ConfigureAwait(false) switch
                {
                    "room not found" => HeartbeatResult.RoomGone,
                    "room closed" => HeartbeatResult.RoomClosed,
                    _ => throw new InvalidOperationException("The room server returned HTTP 404 for a heartbeat."),
                };
            default:
                throw new InvalidOperationException($"The room server returned HTTP {(int)response.StatusCode} for a heartbeat.");
        }
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return (await response.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken: cancellationToken).ConfigureAwait(false))?.Error;
        }
        catch
        {
            return null;
        }
    }

    public async Task<RestoreResult> RestoreRoomAsync(string code, RestoreRoomRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync($"/v1/rooms/{Uri.EscapeDataString(code)}/restore", request, cancellationToken).ConfigureAwait(false);
        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.NoContent => RestoreResult.Restored,
            System.Net.HttpStatusCode.Gone => RestoreResult.Closed,
            System.Net.HttpStatusCode.Conflict => RestoreResult.Taken,
            _ => throw new InvalidOperationException($"The room server returned HTTP {(int)response.StatusCode} restoring the room."),
        };
    }

    /// <returns>True when this member is back in the room; false when the room is not there (yet).</returns>
    public async Task<bool> RejoinAsync(string code, string memberId, RejoinRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync($"/v1/rooms/{Uri.EscapeDataString(code)}/members/{Uri.EscapeDataString(memberId)}/rejoin", request, cancellationToken).ConfigureAwait(false);
        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.NoContent => true,
            System.Net.HttpStatusCode.NotFound => false,
            _ => throw new InvalidOperationException($"The room server refused the rejoin ({await ErrorAsync(response, cancellationToken).ConfigureAwait(false) ?? ((int)response.StatusCode).ToString()})."),
        };
    }

    private sealed record ErrorBody(string? Error);

    public async Task<bool> LeaveAsync(string code, string memberId, string memberSecret, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/v1/rooms/{Uri.EscapeDataString(code)}/members/{Uri.EscapeDataString(memberId)}");
        request.Headers.TryAddWithoutValidation("X-Member-Secret", memberSecret);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.StatusCode == System.Net.HttpStatusCode.NoContent;
    }

    public async Task<bool> UpdateEndpointAsync(string code, string memberId, string memberSecret, EndpointInfo endpoint, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync(
            $"/v1/rooms/{Uri.EscapeDataString(code)}/members/{Uri.EscapeDataString(memberId)}/endpoint",
            new UpdateEndpointRequest(memberSecret, endpoint), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return true;
        }

        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        throw new InvalidOperationException(
            $"The room server did not accept the endpoint update (HTTP {(int)response.StatusCode}).");
    }
}
