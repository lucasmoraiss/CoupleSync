using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CoupleSync.IntegrationTests.Goals;

/// <summary>A11 — PATCH /api/v1/goals/{id} with only the saved amount, and with a deadline of "today".</summary>
[Trait("Category", "Goals")]
public sealed class GoalUpdateIntegrationTests
{
    [Fact]
    public async Task PatchGoal_WithOnlyCurrentAmount_Returns200_AndStoresTheAmount()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);
        var goalId = await CreateGoalAsync(client, DateTime.UtcNow.AddMonths(6));

        var response = await PatchAsync(client, goalId, """{"currentAmount":1500}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1500m, body.GetProperty("currentAmount").GetDecimal());
        Assert.Equal("Viagem", body.GetProperty("title").GetString());
        Assert.Equal(10000m, body.GetProperty("targetAmount").GetDecimal());

        var reloaded = await client.GetFromJsonAsync<JsonElement>($"/api/v1/goals/{goalId}");
        Assert.Equal(1500m, reloaded.GetProperty("currentAmount").GetDecimal());
    }

    [Fact]
    public async Task PatchGoal_ResendingADeadlineOfToday_Returns200()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        // Creation accepts "today" (date only => midnight UTC, which is already behind the clock).
        var today = DateTime.UtcNow.Date;
        var goalId = await CreateGoalAsync(client, today);
        var deadline = today.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        var response = await PatchAsync(client, goalId, $$"""{"currentAmount":250,"deadline":"{{deadline}}"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(250m, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("currentAmount").GetDecimal());
    }

    [Fact]
    public async Task PatchGoal_WithEmptyBody_StillReturns400()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);
        var goalId = await CreateGoalAsync(client, DateTime.UtcNow.AddMonths(6));

        var response = await PatchAsync(client, goalId, "{}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid goalId, string json)
        => client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/goals/{goalId}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

    private static async Task<Guid> CreateGoalAsync(HttpClient client, DateTime deadline)
    {
        var response = await client.PostAsJsonAsync("/api/v1/goals", new
        {
            title = "Viagem",
            targetAmount = 10000m,
            currency = "BRL",
            deadline = DateTime.SpecifyKind(deadline, DateTimeKind.Utc)
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task AuthenticateWithCoupleAsync(HttpClient client)
    {
        const string password = "SecurePass123!";
        var email = $"a11-{Guid.NewGuid():N}@example.com";

        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new { Email = email, Name = "Test User", Password = password });
        register.EnsureSuccessStatusCode();
        var registered = await register.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registered.GetProperty("accessToken").GetString());

        var couple = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, couple.StatusCode);

        client.DefaultRequestHeaders.Authorization = null;
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password = password });
        login.EnsureSuccessStatusCode();
        var loggedIn = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", loggedIn.GetProperty("accessToken").GetString());
    }
}
