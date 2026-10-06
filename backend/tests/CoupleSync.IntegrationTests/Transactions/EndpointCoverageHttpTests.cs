using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleSync.IntegrationTests.Transactions;

/// <summary>
/// TST-05: HTTP coverage of the endpoints that only had service-level tests — incomes (update, delete, month read),
/// reports (spending by category), manual transaction create/delete and goal delete — including authentication,
/// the missing group, group isolation and the error format.
/// </summary>
[Trait("Category", "Http")]
public sealed class EndpointCoverageHttpTests
{
    private sealed record Member(HttpClient Client, Guid UserId, string? JoinCode);

    private static async Task<Member> RegisterAsync(TransactionWebApplicationFactory factory, string name, bool withGroup = true, string? joinCode = null)
    {
        var client = factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = $"http-{Guid.NewGuid():N}@example.com",
            Name = name,
            Password = "SecurePass123!"
        });
        register.EnsureSuccessStatusCode();
        var registered = await register.Content.ReadFromJsonAsync<JsonElement>();
        var userId = registered.GetProperty("user").GetProperty("id").GetGuid();
        UseToken(client, registered.GetProperty("accessToken").GetString()!);

        string? code = joinCode;
        if (joinCode is not null)
        {
            var join = await client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = joinCode });
            join.EnsureSuccessStatusCode();
            UseToken(client, (await join.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);
        }
        else if (withGroup)
        {
            var created = await client.PostAsJsonAsync("/api/v1/couples", new { });
            created.EnsureSuccessStatusCode();
            var couple = await created.Content.ReadFromJsonAsync<JsonElement>();
            UseToken(client, couple.GetProperty("accessToken").GetString()!);
            code = couple.GetProperty("joinCode").GetString();
        }

        return new Member(client, userId, code);
    }

    private static void UseToken(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static string ThisMonth => BrazilTime.MonthOf(DateTime.UtcNow);

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }

    // ---- incomes ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/api/v1/incomes/current")]
    [InlineData("GET", "/api/v1/incomes/2026-09")]
    [InlineData("POST", "/api/v1/incomes")]
    [InlineData("PUT", "/api/v1/incomes/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/v1/incomes/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/v1/reports/spending-by-category")]
    [InlineData("GET", "/api/v1/reports/monthly-trends")]
    [InlineData("POST", "/api/v1/transactions")]
    [InlineData("DELETE", "/api/v1/transactions/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/v1/goals/00000000-0000-0000-0000-000000000001")]
    public async Task Endpoints_WithoutALogin_Return401_AndWithoutAGroup_Return403(string method, string url)
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var anonymous = factory.CreateClient();
        var noToken = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = method is "POST" or "PUT" ? JsonContent.Create(new { }) : null
        });
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        var loner = await RegisterAsync(factory, "Sem grupo", withGroup: false);
        var noGroup = await loner.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = method is "POST" or "PUT" ? JsonContent.Create(new { }) : null
        });
        await AssertErrorAsync(noGroup, HttpStatusCode.Forbidden, "COUPLE_REQUIRED");
    }

    [Fact]
    public async Task Incomes_UpdateAndDelete_OwnerChangesIt_AndTheMonthReadFollows()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");

        var created = await ana.Client.PostAsJsonAsync("/api/v1/incomes",
            new { month = ThisMonth, name = "Salário", amount = 3000m, currency = "BRL", isShared = false });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var updated = await ana.Client.PutAsJsonAsync($"/api/v1/incomes/{id}", new { name = "Salário líquido", amount = 3500.50m });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Salário líquido", body.GetProperty("name").GetString());
        Assert.Equal(3500.50m, body.GetProperty("amount").GetDecimal());

        var month = await ana.Client.GetFromJsonAsync<JsonElement>($"/api/v1/incomes/{ThisMonth}");
        Assert.Equal(3500.50m, month.GetProperty("coupleTotal").GetDecimal());

        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/incomes/{id}")).StatusCode);
        var empty = await ana.Client.GetFromJsonAsync<JsonElement>($"/api/v1/incomes/{ThisMonth}");
        Assert.Equal(0m, empty.GetProperty("coupleTotal").GetDecimal());
        await AssertErrorAsync(await ana.Client.DeleteAsync($"/api/v1/incomes/{id}"), HttpStatusCode.NotFound, "INCOME_SOURCE_NOT_FOUND");
        await AssertErrorAsync(
            await ana.Client.PutAsJsonAsync($"/api/v1/incomes/{Guid.NewGuid()}", new { amount = 1m }),
            HttpStatusCode.NotFound, "INCOME_SOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task Incomes_APersonalIncomeBelongsToItsOwner_AndOtherGroupsCannotSeeIt()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        var bruno = await RegisterAsync(factory, "Bruno", joinCode: ana.JoinCode);
        var stranger = await RegisterAsync(factory, "Outro grupo");

        var created = await ana.Client.PostAsJsonAsync("/api/v1/incomes",
            new { month = ThisMonth, name = "Salário", amount = 3000m, currency = "BRL", isShared = false });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await AssertErrorAsync(await bruno.Client.PutAsJsonAsync($"/api/v1/incomes/{id}", new { amount = 1m }), HttpStatusCode.Forbidden, "INCOME_SOURCE_FORBIDDEN");
        await AssertErrorAsync(await bruno.Client.DeleteAsync($"/api/v1/incomes/{id}"), HttpStatusCode.Forbidden, "INCOME_SOURCE_FORBIDDEN");
        await AssertErrorAsync(await stranger.Client.PutAsJsonAsync($"/api/v1/incomes/{id}", new { amount = 1m }), HttpStatusCode.NotFound, "INCOME_SOURCE_NOT_FOUND");
        await AssertErrorAsync(await stranger.Client.DeleteAsync($"/api/v1/incomes/{id}"), HttpStatusCode.NotFound, "INCOME_SOURCE_NOT_FOUND");
        var strangerView = await stranger.Client.GetFromJsonAsync<JsonElement>($"/api/v1/incomes/{ThisMonth}");
        Assert.Equal(0m, strangerView.GetProperty("coupleTotal").GetDecimal());

        // untouched
        var still = await ana.Client.GetFromJsonAsync<JsonElement>($"/api/v1/incomes/{ThisMonth}");
        Assert.Equal(3000m, still.GetProperty("coupleTotal").GetDecimal());
    }

    [Fact]
    public async Task Incomes_Create_WithInvalidInput_Returns400WithTheErrorFormat()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");

        var response = await ana.Client.PostAsJsonAsync("/api/v1/incomes",
            new { month = "13-2026", name = "", amount = -5m, currency = "BRL", isShared = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("code").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }

    // ---- reports ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reports_SpendingByCategory_GroupsTheCompleteMonths_AndStaysInsideTheGroup()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        var bruno = await RegisterAsync(factory, "Bruno", joinCode: ana.JoinCode);
        var stranger = await RegisterAsync(factory, "Outro grupo");

        // Brasília months, like production: mid-day on the 15th of the previous month cannot cross a boundary.
        var day = BrazilTime.MonthRangeUtc(BrazilTime.AddMonths(ThisMonth, -1)).StartUtc.AddDays(14).AddHours(15);
        async Task AddAsync(HttpClient client, decimal amount, string category) =>
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/transactions", new
            {
                amount, currency = "BRL", eventTimestampUtc = day, description = $"{category} {amount}", category
            })).StatusCode);
        await AddAsync(ana.Client, 100m, "ALIMENTACAO");
        await AddAsync(bruno.Client, 50m, "ALIMENTACAO");
        await AddAsync(ana.Client, 50m, "LAZER");
        await AddAsync(stranger.Client, 999m, "COMPRAS");

        var report = await bruno.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports/spending-by-category?months=3");

        var categories = report.GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal(2, categories.Count);
        Assert.Equal(200m, categories.Sum(c => c.GetProperty("total").GetDecimal()));
        var first = categories[0];
        Assert.Equal(150m, first.GetProperty("total").GetDecimal());
        Assert.Equal(75m, first.GetProperty("percentage").GetDecimal());
        var empty = await stranger.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports/spending-by-category?months=3");
        Assert.Equal(1, empty.GetProperty("categories").GetArrayLength()); // only its own R$ 999
        Assert.Equal(999m, empty.GetProperty("categories")[0].GetProperty("total").GetDecimal());
    }

    [Theory]
    [InlineData("/api/v1/reports/spending-by-category?months=0")]
    [InlineData("/api/v1/reports/spending-by-category?months=61")]
    [InlineData("/api/v1/reports/monthly-trends?months=0")]
    [InlineData("/api/v1/reports/monthly-trends?months=61")]
    public async Task Reports_MonthsOutsideTheRange_Return400(string url)
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");

        await AssertErrorAsync(await ana.Client.GetAsync(url), HttpStatusCode.BadRequest, "INVALID_MONTHS");
    }

    // ---- transactions -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Transactions_CreateThenDelete_RemovesItForTheWholeGroup_AndOthersCannotDeleteIt()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        var bruno = await RegisterAsync(factory, "Bruno", joinCode: ana.JoinCode);
        var stranger = await RegisterAsync(factory, "Outro grupo");

        var created = await ana.Client.PostAsJsonAsync("/api/v1/transactions", new
        {
            amount = 42.90m, currency = "BRL", description = "Almoço", merchant = "Restaurante", category = "ALIMENTACAO"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal(42.90m, body.GetProperty("amount").GetDecimal());
        Assert.Equal("ALIMENTACAO", body.GetProperty("category").GetString());
        Assert.Equal("Manual", body.GetProperty("source").GetString());

        Assert.Equal(1, (await bruno.Client.GetFromJsonAsync<JsonElement>("/api/v1/transactions")).GetProperty("totalCount").GetInt32());
        await AssertErrorAsync(await stranger.Client.DeleteAsync($"/api/v1/transactions/{id}"), HttpStatusCode.NotFound, "TRANSACTION_NOT_FOUND");
        Assert.Equal(1, (await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/transactions")).GetProperty("totalCount").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await bruno.Client.DeleteAsync($"/api/v1/transactions/{id}")).StatusCode);
        Assert.Equal(0, (await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/transactions")).GetProperty("totalCount").GetInt32());
        await AssertErrorAsync(await ana.Client.DeleteAsync($"/api/v1/transactions/{id}"), HttpStatusCode.NotFound, "TRANSACTION_NOT_FOUND");
    }

    [Fact]
    public async Task Transactions_Create_WithInvalidInput_Returns400WithTheErrorFormat()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");

        foreach (var invalid in new object[]
        {
            new { amount = 0m, currency = "BRL", category = "LAZER" },
            new { amount = 10m, currency = "USD", category = "LAZER" },
            new { amount = 10m, currency = "BRL", category = "" },
        })
        {
            var response = await ana.Client.PostAsJsonAsync("/api/v1/transactions", invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("code").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        }

        Assert.Equal(0, (await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/transactions")).GetProperty("totalCount").GetInt32());
    }

    // ---- goals --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Goals_Delete_RemovesTheGoal_KeepsItsTransactions_AndIsolatesGroups()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        var stranger = await RegisterAsync(factory, "Outro grupo");

        var goal = await ana.Client.PostAsJsonAsync("/api/v1/goals", new
        {
            title = "Viagem", targetAmount = 1000m, currency = "BRL", deadline = DateTime.UtcNow.AddMonths(6)
        });
        Assert.Equal(HttpStatusCode.Created, goal.StatusCode);
        var goalId = (await goal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var transaction = await ana.Client.PostAsJsonAsync("/api/v1/transactions", new
        {
            amount = 100m, currency = "BRL", description = "Passagem", category = "LAZER"
        });
        var transactionId = (await transaction.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var link = await ana.Client.PatchAsJsonAsync($"/api/v1/transactions/{transactionId}/goal", new { goalId });
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);

        await AssertErrorAsync(await stranger.Client.DeleteAsync($"/api/v1/goals/{goalId}"), HttpStatusCode.NotFound, "GOAL_NOT_FOUND");
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.GetAsync($"/api/v1/goals/{goalId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/goals/{goalId}")).StatusCode);

        await AssertErrorAsync(await ana.Client.GetAsync($"/api/v1/goals/{goalId}"), HttpStatusCode.NotFound, "GOAL_NOT_FOUND");
        await AssertErrorAsync(await ana.Client.DeleteAsync($"/api/v1/goals/{goalId}"), HttpStatusCode.NotFound, "GOAL_NOT_FOUND");
        var transactions = await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/transactions");
        Assert.Equal(1, transactions.GetProperty("totalCount").GetInt32()); // the user's transaction is never deleted with the goal
        // The API does not expose goalId, so check the stored row: still there, no longer linked to the deleted goal.
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Transactions
            .IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == transactionId);
        Assert.Null(stored.GoalId);
    }
}
