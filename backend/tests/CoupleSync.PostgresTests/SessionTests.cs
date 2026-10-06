using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Infrastructure.Persistence;

namespace CoupleSync.PostgresTests;

/// <summary>Refresh-token rotation, logout and revocation: the single-statement UPDATE/DELETE paths on PostgreSQL.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SessionTests
{
    private readonly PostgresServer _server;

    public SessionTests(PostgresServer server) => _server = server;

    [PostgresFact]
    public async Task Refresh_RotatesOnce_Logout_UnregistersTheDevice_AndRevokeAllEndsEverySession()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var ana = await factory.RegisterAsync("Ana");

        // Five refreshes with the same token at once: the conditional UPDATE lets exactly one through.
        var gate = new TaskCompletionSource();
        var attempts = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            using var client = factory.CreateClient();
            return await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = ana.RefreshToken });
        })).ToList();
        gate.SetResult();
        var responses = await Task.WhenAll(attempts);
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        var rotated = (await responses.Single(r => r.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("refreshToken").GetString()!;

        // Logout removes the refresh token and this device's push token; the old session cannot be refreshed again.
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.PostAsJsonAsync("/api/v1/devices/token", new { token = "phone-1", platform = "android" })).StatusCode);
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM device_tokens"));
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = rotated, deviceToken = "phone-1" })).StatusCode);
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM device_tokens"));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM refresh_tokens"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = rotated })).StatusCode);

        // Revoking every session of a user (password reset) deletes its refresh token row.
        var bruno = await factory.RegisterAsync("Bruno", createGroup: false);
        await using var db = factory.NewContext();
        Assert.Equal(1, await new AuthRepository(db).RevokeRefreshTokensByUserIdAsync(bruno.UserId, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = bruno.RefreshToken })).StatusCode);
    }
}
