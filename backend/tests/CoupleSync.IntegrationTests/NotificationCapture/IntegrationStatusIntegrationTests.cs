using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleSync.IntegrationTests.NotificationCapture;

public sealed class IntegrationStatusIntegrationTests
{
    [Fact]
    public async Task GetStatus_WithoutAuth_ShouldReturn401()
    {
        await using var factory = new NotificationCaptureWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/integrations/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetStatus_AuthenticatedButNoCouple_ShouldReturn403()
    {
        await using var factory = new NotificationCaptureWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterAndGetTokenAsync(client, $"status-no-couple-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/integrations/status");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetStatus_EmptyState_ShouldReturn200WithZeroCounts()
    {
        await using var factory = new NotificationCaptureWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"status-empty-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/integrations/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<IntegrationStatusDto>();
        Assert.NotNull(payload);
        Assert.False(payload!.IsActive);
        Assert.Null(payload.LastEventAtUtc);
        Assert.Equal(0, payload.Counts.TotalAccepted);
        Assert.Equal(0, payload.Counts.TotalDuplicate);
        Assert.Equal(0, payload.Counts.TotalRejected);
    }

    [Fact]
    public async Task GetStatus_AfterIngestingEvents_ShouldReflectCounts()
    {
        await using var factory = new NotificationCaptureWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"status-ingest-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Ingest a valid event
        var ingestRequest = new IngestRequestDto(
            Bank: "NUBANK",
            Amount: 50m,
            Currency: "BRL",
            EventTimestamp: DateTime.UtcNow.AddMinutes(-5),
            Description: "Coffee",
            Merchant: "STARBUCKS",
            RawNotificationText: "Nubank: R$50 at Starbucks");
        var ingestResponse = await client.PostAsJsonAsync("/api/v1/integrations/events", ingestRequest);
        Assert.Equal(HttpStatusCode.Created, ingestResponse.StatusCode);

        var response = await client.GetAsync("/api/v1/integrations/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<IntegrationStatusDto>();
        Assert.NotNull(payload);
        Assert.True(payload!.IsActive);
        Assert.NotNull(payload.LastEventAtUtc);
        Assert.Equal(1, payload.Counts.TotalAccepted);
    }

    [Fact]
    public async Task GetStatus_AfterDuplicate_ShouldReflectDuplicateCount()
    {
        await using var factory = new NotificationCaptureWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"status-dup-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var ingestRequest = new IngestRequestDto(
            Bank: "NUBANK",
            Amount: 75m,
            Currency: "BRL",
            EventTimestamp: DateTime.UtcNow.AddMinutes(-5),
            Description: "Lunch",
            Merchant: "RESTAURANT",
            RawNotificationText: "Nubank: R$75 at Restaurant");

        // First event
        await client.PostAsJsonAsync("/api/v1/integrations/events", ingestRequest);
        // Duplicate event
        await client.PostAsJsonAsync("/api/v1/integrations/events", ingestRequest);

        var response = await client.GetAsync("/api/v1/integrations/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<IntegrationStatusDto>();
        Assert.NotNull(payload);
        Assert.Equal(1, payload!.Counts.TotalAccepted);
        Assert.Equal(1, payload.Counts.TotalDuplicate);
    }

    private static async Task<string> RegisterAndGetTokenAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = email,
            Name = "Test User",
            Password = "SecurePass123!"
        });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
        return auth!.AccessToken;
    }

    private static async Task<string> RegisterWithCoupleAndGetTokenAsync(HttpClient client, string email)
    {
        const string password = "SecurePass123!";

        var registerResponse = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = email,
            Name = "Test User",
            Password = password
        });
        registerResponse.EnsureSuccessStatusCode();
        var registerAuth = await registerResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registerAuth!.AccessToken);

        var createCoupleResponse = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, createCoupleResponse.StatusCode);

        client.DefaultRequestHeaders.Authorization = null;
        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Email = email,
            Password = password
        });
        loginResponse.EnsureSuccessStatusCode();
        var loginAuth = await loginResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        return loginAuth!.AccessToken;
    }

    private sealed record AuthResponseDto(AuthUserDto User, string AccessToken, string RefreshToken);
    private sealed record AuthUserDto(Guid Id, string Email, string Name);
    private sealed record IngestRequestDto(
        string Bank,
        decimal Amount,
        string Currency,
        DateTime EventTimestamp,
        string? Description,
        string? Merchant,
        string? RawNotificationText);
    private sealed record IntegrationStatusDto(
        bool IsActive,
        DateTime? LastEventAtUtc,
        DateTime? LastErrorAtUtc,
        string? LastErrorMessage,
        string? RecoveryHint,
        IntegrationCountsDto Counts);
    private sealed record IntegrationCountsDto(
        int TotalAccepted,
        int TotalDuplicate,
        int TotalRejected);
}
