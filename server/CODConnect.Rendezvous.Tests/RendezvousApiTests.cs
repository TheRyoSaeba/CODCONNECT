using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CODConnect.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CODConnect.Rendezvous.Tests;

public class RendezvousApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly EndpointInfo TestApiEndpoint = new(["192.168.1.10"], 47777);

    private sealed record ApiError(string Error);

    private static WebApplicationFactory<Program> CreateFactory(string? rateLimitPerMinute)
    {
        Environment.SetEnvironmentVariable("CODCONNECT_ROOM_TTL_MINUTES", null);
        Environment.SetEnvironmentVariable("CODCONNECT_MAX_MEMBERS", null);
        Environment.SetEnvironmentVariable("CODCONNECT_RATE_LIMIT_PER_MIN", rateLimitPerMinute);
        return new WebApplicationFactory<Program>();
    }

    private static async Task<CreateRoomResponse> CreateRoomAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/v1/rooms", new CreateRoomRequest("host", new EndpointInfo(["192.168.1.10"], 47777)), JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreateRoomResponse>(JsonOptions);
        Assert.NotNull(created);
        return created!;
    }

    private static async Task<ApiError> AssertJsonErrorAsync(HttpResponseMessage response, HttpStatusCode statusCode, string message)
    {
        Assert.Equal(statusCode, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal(message, error!.Error);
        return error;
    }

    [Fact]
    public async Task FullLifecycle_Create_Join_Get_Delete_ThenGone()
    {
        using var factory = CreateFactory(rateLimitPerMinute: "1000");
        using var client = factory.CreateClient();

        var created = await CreateRoomAsync(client);
        Assert.True(RoomCode.TryValidate(created.RoomCode, out _));
        Assert.Matches("^[0-9A-F]{16}$", created.AdminKey);
        Assert.True(created.ExpiresUtc > DateTimeOffset.UtcNow);

        using (var joinResponse = await client.PostAsJsonAsync(
            $"/v1/rooms/{created.RoomCode}/join", new JoinRequest("alice"), JsonOptions))
        {
            Assert.Equal(HttpStatusCode.OK, joinResponse.StatusCode);
            var joined = await joinResponse.Content.ReadFromJsonAsync<JoinResponse>(JsonOptions);
            Assert.NotNull(joined);
            Assert.Matches("^[0-9a-f]{12}$", joined!.MemberId);
            Assert.Equal(created.RoomCode, joined.RoomCode);
            Assert.Equal(created.ExpiresUtc, joined.ExpiresUtc);

            using var getResponse = await client.GetAsync($"/v1/rooms/{created.RoomCode}");
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
            var info = await getResponse.Content.ReadFromJsonAsync<RoomInfo>(JsonOptions);
            Assert.NotNull(info);
            Assert.Equal(created.RoomCode, info!.RoomCode);
            Assert.Equal(2, info.MemberCount);
            Assert.Contains(info.Members, m => m.DisplayName == "alice" && m.MemberId == joined.MemberId);
        }

        using (var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/v1/rooms/{created.RoomCode}"))
        {
            deleteRequest.Headers.Add("X-Admin-Key", created.AdminKey);
            using var deleteResponse = await client.SendAsync(deleteRequest);
            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v1/rooms/{created.RoomCode}")).StatusCode);
        using (var joinAfterClose = await client.PostAsJsonAsync(
            $"/v1/rooms/{created.RoomCode}/join", new JoinRequest("bob"), JsonOptions))
        {
            await AssertJsonErrorAsync(joinAfterClose, HttpStatusCode.NotFound, "room not found");
        }
    }

    [Fact]
    public async Task Join_UnknownRoom_Returns404_RoomNotFound()
    {
        using var factory = CreateFactory(rateLimitPerMinute: "1000");
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/rooms/ZZZZ-ZZ/join", new JoinRequest("alice"), JsonOptions);

        await AssertJsonErrorAsync(response, HttpStatusCode.NotFound, "room not found");
    }

    [Fact]
    public async Task Join_InvalidName_Returns400_InvalidName()
    {
        using var factory = CreateFactory(rateLimitPerMinute: "1000");
        using var client = factory.CreateClient();
        var created = await CreateRoomAsync(client);

        using (var blank = await client.PostAsJsonAsync(
            $"/v1/rooms/{created.RoomCode}/join", new JoinRequest("   "), JsonOptions))
        {
            await AssertJsonErrorAsync(blank, HttpStatusCode.BadRequest, "invalid name");
        }

        using (var tooLong = await client.PostAsJsonAsync(
            $"/v1/rooms/{created.RoomCode}/join", new JoinRequest(new string('x', 33)), JsonOptions))
        {
            await AssertJsonErrorAsync(tooLong, HttpStatusCode.BadRequest, "invalid name");
        }
    }

    [Fact]
    public async Task Join_DuplicateOrInvisibleName_IsRefused()
    {
        using var factory = CreateFactory(rateLimitPerMinute: "1000");
        using var client = factory.CreateClient();
        var created = await CreateRoomAsync(client);

        using (var first = await client.PostAsJsonAsync($"/v1/rooms/{created.RoomCode}/join", new JoinRequest("Kyle"), JsonOptions))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var twin = await client.PostAsJsonAsync($"/v1/rooms/{created.RoomCode}/join", new JoinRequest("KYLE"), JsonOptions);
        await AssertJsonErrorAsync(twin, HttpStatusCode.Conflict, "name taken");

        foreach (var sneaky in new[] { "Ky\u200Ble", "\u202Eelyk", "Kyle\n", " Kyle" })
        {
            using var response = await client.PostAsJsonAsync($"/v1/rooms/{created.RoomCode}/join", new JoinRequest(sneaky), JsonOptions);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Join_BeyondCapacity_Returns409_RoomFull()
    {
        using var factory = CreateFactory(rateLimitPerMinute: "1000");
        using var client = factory.CreateClient();
        var created = await CreateRoomAsync(client);

        for (var i = 0; i < 5; i++)
        {
            using var response = await client.PostAsJsonAsync(
                $"/v1/rooms/{created.RoomCode}/join", new JoinRequest($"p{i}"), JsonOptions);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var overflow = await client.PostAsJsonAsync(
            $"/v1/rooms/{created.RoomCode}/join", new JoinRequest("extra"), JsonOptions);

        await AssertJsonErrorAsync(overflow, HttpStatusCode.Conflict, "room full");
    }

    [Fact]
    public async Task Delete_WithWrongOrMissingAdminKey_Returns403_Forbidden()
    {
        using var factory = CreateFactory(rateLimitPerMinute: "1000");
        using var client = factory.CreateClient();
        var created = await CreateRoomAsync(client);

        using (var wrongKeyRequest = new HttpRequestMessage(HttpMethod.Delete, $"/v1/rooms/{created.RoomCode}"))
        {
            wrongKeyRequest.Headers.Add("X-Admin-Key", "0123456789ABCDEF");
            using var wrongKeyResponse = await client.SendAsync(wrongKeyRequest);
            await AssertJsonErrorAsync(wrongKeyResponse, HttpStatusCode.Forbidden, "forbidden");
        }

        using (var missingKeyRequest = new HttpRequestMessage(HttpMethod.Delete, $"/v1/rooms/{created.RoomCode}"))
        {
            using var missingKeyResponse = await client.SendAsync(missingKeyRequest);
            await AssertJsonErrorAsync(missingKeyResponse, HttpStatusCode.Forbidden, "forbidden");
        }

        using var getResponse = await client.GetAsync($"/v1/rooms/{created.RoomCode}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_RateLimited_Returns429_WhenLimitExceeded()
    {
        using var factory = CreateFactory(rateLimitPerMinute: "2");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/v1/rooms", new CreateRoomRequest("h1", TestApiEndpoint), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/v1/rooms", new CreateRoomRequest("h2", TestApiEndpoint), JsonOptions)).StatusCode);

        using var third = await client.PostAsJsonAsync("/v1/rooms", new CreateRoomRequest("h3", TestApiEndpoint), JsonOptions);
        await AssertJsonErrorAsync(third, HttpStatusCode.TooManyRequests, "rate limited");
    }

    [Fact]
    public async Task Get_IsNotRateLimited()
    {
        using var factory = CreateFactory(rateLimitPerMinute: "1");
        using var client = factory.CreateClient();
        var created = await CreateRoomAsync(client);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v1/rooms/{created.RoomCode}")).StatusCode);
        }
    }

    [Fact]
    public async Task Heartbeat_And_Leave_OverHttp()
    {
        using var factory = CreateFactory(rateLimitPerMinute: null);
        using var client = factory.CreateClient();
        var rendezvous = new RendezvousClient(client);

        var created = await CreateRoomAsync(client);
        var joined = await rendezvous.JoinRoomAsync(created.RoomCode, "friend");
        Assert.NotNull(joined);

        Assert.Equal(HeartbeatResult.Alive, await rendezvous.HeartbeatAsync(created.RoomCode, created.MemberId, created.MemberSecret));
        Assert.Equal(HeartbeatResult.Alive, await rendezvous.HeartbeatAsync(created.RoomCode, joined!.MemberId, joined.MemberSecret));
        Assert.Equal(HeartbeatResult.MemberGone, await rendezvous.HeartbeatAsync(created.RoomCode, joined.MemberId, "wrong"));

        Assert.True(await rendezvous.LeaveAsync(created.RoomCode, joined.MemberId, joined.MemberSecret));
        Assert.Equal(HeartbeatResult.MemberGone, await rendezvous.HeartbeatAsync(created.RoomCode, joined.MemberId, joined.MemberSecret));
        Assert.Equal(1, (await rendezvous.GetRoomAsync(created.RoomCode))!.MemberCount);

        Assert.True(await rendezvous.LeaveAsync(created.RoomCode, created.MemberId, created.MemberSecret));
        Assert.Equal(HeartbeatResult.RoomClosed, await rendezvous.HeartbeatAsync(created.RoomCode, created.MemberId, created.MemberSecret));
        Assert.Null(await rendezvous.GetRoomAsync(created.RoomCode));
    }

    [Fact]
    public async Task Heartbeat_AgainstServerWithoutTheRoute_IsNotRoomGone()
    {
        using var http = new HttpClient(new StaticHandler(HttpStatusCode.NotFound)) { BaseAddress = new Uri("http://old-server/") };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new RendezvousClient(http).HeartbeatAsync("ABCD-EF", "m", "s"));
    }

    private sealed class StaticHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status));
    }
}
