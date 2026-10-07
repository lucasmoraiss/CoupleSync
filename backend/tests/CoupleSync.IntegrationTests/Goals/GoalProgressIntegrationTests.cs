using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CoupleSync.IntegrationTests.Goals;

/// <summary>C05 — goal progress is manual amount + linked transactions, identical on every endpoint.</summary>
[Trait("Category", "Goals")]
public sealed class GoalProgressIntegrationTests
{
    [Fact]
    public async Task EveryEndpoint_ReturnsTheSameUnifiedProgress_AndItFollowsLinkUnlinkAndDelete()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);
        var goalId = await CreateGoalAsync(client, target: 1000m);

        var patch = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/goals/{goalId}")
        {
            Content = new StringContent("""{"manualAmount":100}""", Encoding.UTF8, "application/json")
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var tx1 = await CreateTransactionAsync(client, 250m);
        var tx2 = await CreateTransactionAsync(client, 150m);
        await LinkAsync(client, tx1, goalId);
        await LinkAsync(client, tx2, goalId);

        await AssertAllEndpointsAsync(client, goalId, manual: 100m, linked: 400m, total: 500m, percent: 50m, achieved: false);

        // Desvincular reflete na leitura seguinte.
        await LinkAsync(client, tx2, null);
        await AssertAllEndpointsAsync(client, goalId, manual: 100m, linked: 250m, total: 350m, percent: 35m, achieved: false);

        // Excluir a transação vinculada também.
        var delete = await client.DeleteAsync($"/api/v1/transactions/{tx1}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await AssertAllEndpointsAsync(client, goalId, manual: 100m, linked: 0m, total: 100m, percent: 10m, achieved: false);
    }

    [Fact]
    public async Task AchievedGoal_IsFlaggedTheSameOnEveryEndpoint()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);
        var goalId = await CreateGoalAsync(client, target: 500m);
        var tx = await CreateTransactionAsync(client, 600m);
        await LinkAsync(client, tx, goalId);

        await AssertAllEndpointsAsync(client, goalId, manual: 0m, linked: 600m, total: 600m, percent: 100m, achieved: true);
    }

    [Fact]
    public async Task LegacyApp_ResendingTheDisplayedTotalAsCurrentAmount_DoesNotDoubleCountLinkedTransactions()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);
        var goalId = await CreateGoalAsync(client, target: 1000m);
        var tx = await CreateTransactionAsync(client, 300m);
        await LinkAsync(client, tx, goalId);

        var patch = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/goals/{goalId}")
        {
            Content = new StringContent("""{"title":"Viagem 2","currentAmount":300}""", Encoding.UTF8, "application/json")
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        await AssertAllEndpointsAsync(client, goalId, manual: 0m, linked: 300m, total: 300m, percent: 30m, achieved: false);
    }

    private static async Task AssertAllEndpointsAsync(
        HttpClient client, Guid goalId, decimal manual, decimal linked, decimal total, decimal percent, bool achieved)
    {
        var byId = await client.GetFromJsonAsync<JsonElement>($"/api/v1/goals/{goalId}");
        var list = (await client.GetFromJsonAsync<JsonElement>("/api/v1/goals")).GetProperty("items").EnumerateArray()
            .Single(g => g.GetProperty("id").GetGuid() == goalId);
        var summary = (await client.GetFromJsonAsync<JsonElement>("/api/v1/goals/progress-summary")).GetProperty("goals").EnumerateArray()
            .Single(g => g.GetProperty("id").GetGuid() == goalId);
        var progress = await client.GetFromJsonAsync<JsonElement>($"/api/v1/goals/{goalId}/progress");

        foreach (var g in new[] { byId, list, summary })
        {
            Assert.Equal(total, g.GetProperty("currentAmount").GetDecimal());
            Assert.Equal(manual, g.GetProperty("manualAmount").GetDecimal());
            Assert.Equal(linked, g.GetProperty("linkedAmount").GetDecimal());
            Assert.Equal(percent, g.GetProperty("progressPercent").GetDecimal());
            Assert.Equal(achieved, g.GetProperty("isAchieved").GetBoolean());
        }

        Assert.Equal(total, progress.GetProperty("contributedAmount").GetDecimal());
        Assert.Equal(manual, progress.GetProperty("manualAmount").GetDecimal());
        Assert.Equal(linked, progress.GetProperty("linkedAmount").GetDecimal());
        Assert.Equal(percent, progress.GetProperty("progressPercent").GetDecimal());
        Assert.Equal(achieved, progress.GetProperty("isAchieved").GetBoolean());
    }

    private static async Task<Guid> CreateGoalAsync(HttpClient client, decimal target)
    {
        var response = await client.PostAsJsonAsync("/api/v1/goals", new
        {
            title = "Viagem",
            targetAmount = target,
            currency = "BRL",
            deadline = DateTime.UtcNow.AddMonths(6)
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateTransactionAsync(HttpClient client, decimal amount)
    {
        var response = await client.PostAsJsonAsync("/api/v1/transactions", new
        {
            amount,
            currency = "BRL",
            description = "Guardado",
            category = "Outros"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task LinkAsync(HttpClient client, Guid transactionId, Guid? goalId)
    {
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/transactions/{transactionId}/goal")
        {
            Content = JsonContent.Create(new { goalId })
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task AuthenticateWithCoupleAsync(HttpClient client)
    {
        const string password = "SecurePass123!";
        var email = $"c05-{Guid.NewGuid():N}@example.com";

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
