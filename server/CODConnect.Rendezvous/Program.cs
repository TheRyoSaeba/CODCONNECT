using System.Globalization;
using Microsoft.AspNetCore.HttpOverrides;
using CODConnect.Protocol;
using CODConnect.Rendezvous;

var builder = WebApplication.CreateBuilder(args);

if (TryGetPositiveInt("PORT", out var port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddSingleton(new RoomStore(BuildRoomStoreOptions()));
builder.Services.AddSingleton(new RateLimiter(BuildRateLimiterOptions()));
builder.Services.AddHostedService<RoomSweeper>();

var app = builder.Build();
app.UseForwardedHeaders();

app.MapGet("/healthz", () => Results.Text("ok"));

static string GetRateLimitKey(HttpContext context) =>
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

static IResult JsonError(int statusCode, string message) =>
    Results.Json(new { error = message }, statusCode: statusCode);

static IResult RateLimited() => JsonError(StatusCodes.Status429TooManyRequests, "rate limited");

app.MapPost("/v1/rooms", (HttpContext context, CreateRoomRequest? request, RoomStore store, RateLimiter limiter) =>
{
    if (!limiter.Allow(GetRateLimitKey(context)))
    {
        return RateLimited();
    }

    if (request is null)
    {
        return JsonError(StatusCodes.Status400BadRequest, "invalid request");
    }

    try
    {
        var created = store.Create(request.DisplayName, request.Endpoint);
        return Results.Json(
            new CreateRoomResponse(created.RoomCode, created.AdminKey, created.MemberId, created.MemberSecret, created.ExpiresUtc),
            statusCode: StatusCodes.Status201Created);
    }
    catch (ArgumentException)
    {
        return JsonError(StatusCodes.Status400BadRequest, "invalid request");
    }
});

app.MapPut("/v1/rooms/{code}/members/{memberId}/endpoint", (string code, string memberId, UpdateEndpointRequest? request, RoomStore store) =>
{
    if (request is null)
    {
        return JsonError(StatusCodes.Status400BadRequest, "invalid request");
    }

    return store.UpdateEndpoint(code, memberId, request.Secret, request.Endpoint)
        ? Results.NoContent()
        : JsonError(StatusCodes.Status403Forbidden, "forbidden");
});

app.MapPost("/v1/rooms/{code}/join", (HttpContext context, string code, JoinRequest? request, RoomStore store, RateLimiter limiter) =>
{
    if (!limiter.Allow(GetRateLimitKey(context)))
    {
        return RateLimited();
    }

    try
    {
        var outcome = store.Join(code, request?.DisplayName ?? string.Empty, request?.Endpoint);
        return outcome.Status switch
        {
            JoinStatus.Joined => Results.Ok(outcome.Response),
            JoinStatus.UnknownRoom => JsonError(StatusCodes.Status404NotFound, "room not found"),
            JoinStatus.RoomFull => JsonError(StatusCodes.Status409Conflict, "room full"),
            JoinStatus.NameTaken => JsonError(StatusCodes.Status409Conflict, "name taken"),
            _ => JsonError(StatusCodes.Status400BadRequest, "invalid name"),
        };
    }
    catch (ArgumentException)
    {
        return JsonError(StatusCodes.Status400BadRequest, "invalid request");
    }
});

app.MapPost("/v1/rooms/{code}/members/{memberId}/heartbeat", (string code, string memberId, HeartbeatRequest? request, RoomStore store) =>
{
    if (request is null)
    {
        return JsonError(StatusCodes.Status400BadRequest, "invalid request");
    }

    return store.Heartbeat(code, memberId, request.Secret) switch
    {
        HeartbeatStatus.Alive => Results.NoContent(),
        HeartbeatStatus.RoomGone => JsonError(StatusCodes.Status404NotFound, "room not found"),
        HeartbeatStatus.RoomClosed => JsonError(StatusCodes.Status404NotFound, "room closed"),
        HeartbeatStatus.MemberGone => JsonError(StatusCodes.Status410Gone, "member gone"),
        _ => JsonError(StatusCodes.Status403Forbidden, "forbidden"),
    };
});

app.MapPost("/v1/rooms/{code}/restore", (string code, RestoreRoomRequest? request, RoomStore store) =>
{
    if (request is null)
    {
        return JsonError(StatusCodes.Status400BadRequest, "invalid request");
    }

    try
    {
        return store.Restore(code, request) switch
        {
            RestoreResult.Restored => Results.NoContent(),
            RestoreResult.Closed => JsonError(StatusCodes.Status410Gone, "room closed"),
            _ => JsonError(StatusCodes.Status409Conflict, "room taken"),
        };
    }
    catch (ArgumentException)
    {
        return JsonError(StatusCodes.Status400BadRequest, "invalid request");
    }
});

app.MapPost("/v1/rooms/{code}/members/{memberId}/rejoin", (string code, string memberId, RejoinRequest? request, RoomStore store) =>
{
    if (request is null)
    {
        return JsonError(StatusCodes.Status400BadRequest, "invalid request");
    }

    try
    {
        return store.Rejoin(code, memberId, request) switch
        {
            JoinStatus.Joined => Results.NoContent(),
            JoinStatus.UnknownRoom => JsonError(StatusCodes.Status404NotFound, "room not found"),
            JoinStatus.RoomFull => JsonError(StatusCodes.Status409Conflict, "room full"),
            JoinStatus.NameTaken => JsonError(StatusCodes.Status409Conflict, "name taken"),
            _ => JsonError(StatusCodes.Status400BadRequest, "invalid name"),
        };
    }
    catch (ArgumentException)
    {
        return JsonError(StatusCodes.Status400BadRequest, "invalid request");
    }
});

app.MapDelete("/v1/rooms/{code}/members/{memberId}", (string code, string memberId, HttpContext context, RoomStore store) =>
{
    var secret = context.Request.Headers["X-Member-Secret"].FirstOrDefault() ?? string.Empty;
    return store.Leave(code, memberId, secret)
        ? Results.NoContent()
        : JsonError(StatusCodes.Status404NotFound, "not found");
});

app.MapGet("/v1/rooms/{code}", (string code, RoomStore store) =>
{
    var info = store.Get(code);
    return info is null ? Results.NotFound() : Results.Ok(info);
});

app.MapDelete("/v1/rooms/{code}", (string code, HttpContext context, RoomStore store) =>
{
    var adminKey = context.Request.Headers["X-Admin-Key"].FirstOrDefault() ?? string.Empty;
    if (store.Close(code, adminKey))
    {
        return Results.NoContent();
    }

    return store.Get(code) is null
        ? Results.NotFound()
        : JsonError(StatusCodes.Status403Forbidden, "forbidden");
});

app.Run();

static RoomStoreOptions BuildRoomStoreOptions()
{
    var options = new RoomStoreOptions();
    if (TryGetPositiveDouble("CODCONNECT_ROOM_TTL_MINUTES", out var ttlMinutes))
    {
        options = options with { RoomTtl = TimeSpan.FromMinutes(ttlMinutes) };
    }

    if (TryGetPositiveInt("CODCONNECT_MAX_MEMBERS", out var maxMembers))
    {
        options = options with { MaxMembers = maxMembers };
    }

    if (TryGetPositiveDouble("CODCONNECT_MEMBER_TIMEOUT_SECONDS", out var memberTimeoutSeconds))
    {
        options = options with { MemberTimeout = TimeSpan.FromSeconds(memberTimeoutSeconds) };
    }

    return options;
}

static RateLimiterOptions BuildRateLimiterOptions()
{
    if (TryGetPositiveInt("CODCONNECT_RATE_LIMIT_PER_MIN", out var perMinute))
    {
        return new RateLimiterOptions { RequestsPerWindow = perMinute };
    }

    return new RateLimiterOptions();
}

static bool TryGetPositiveInt(string name, out int value)
{
    value = 0;
    if (int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        && parsed > 0)
    {
        value = parsed;
        return true;
    }

    return false;
}

static bool TryGetPositiveDouble(string name, out double value)
{
    value = 0;
    if (double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        && double.IsFinite(parsed)
        && parsed > 0)
    {
        value = parsed;
        return true;
    }

    return false;
}

internal sealed class RoomSweeper(RoomStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                store.SweepExpired();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public partial class Program { }
