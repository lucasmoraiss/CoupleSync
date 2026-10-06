using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.IntegrationTests.Transactions;

namespace CoupleSync.IntegrationTests.Income;

/// <summary>S3 3.82 / DAD-17: recurring income carries forward and feeds reports and the cash-flow projection.</summary>
[Trait("Category", "Income")]
public sealed class RecurringIncomeIntegrationTests
{
    [Fact]
    public async Task RecurringIncome_CarriesForward_AndShowsUpInReportsAndCashFlow()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await RegisterWithCoupleAsync(client);

        var current = BrazilTime.MonthOf(DateTime.UtcNow);
        var twoAgo = BrazilTime.AddMonths(current, -2);
        var oneAgo = BrazilTime.AddMonths(current, -1);
        var threeAgo = BrazilTime.AddMonths(current, -3);

        await AddIncomeAsync(client, twoAgo, "Salário", 5000m, recurring: true);
        await AddIncomeAsync(client, oneAgo, "Bônus", 700m, recurring: false);

        Assert.Equal(0m, await CoupleTotalAsync(client, threeAgo));
        Assert.Equal(5000m, await CoupleTotalAsync(client, twoAgo));
        Assert.Equal(5700m, await CoupleTotalAsync(client, oneAgo));
        Assert.Equal(5000m, await CoupleTotalAsync(client, current));

        var trends = await client.GetFromJsonAsync<JsonElement>("/api/v1/reports/monthly-trends?months=3");
        var months = trends.GetProperty("months").EnumerateArray().ToList();
        Assert.Equal(new[] { twoAgo, oneAgo, current }, months.Select(m => m.GetProperty("month").GetString()).ToArray());
        Assert.Equal(new[] { 5000m, 5700m, 5000m }, months.Select(m => m.GetProperty("income").GetDecimal()).ToArray());
        Assert.Equal(new[] { 5000m, 5700m, 5000m }, months.Select(m => m.GetProperty("net").GetDecimal()).ToArray());

        var cashFlow = await client.GetFromJsonAsync<JsonElement>("/api/v1/cashflow?horizon=30");
        Assert.Equal(current, cashFlow.GetProperty("month").GetString());
        Assert.Equal(5000m, cashFlow.GetProperty("monthIncome").GetDecimal());
        Assert.Equal(5000m, cashFlow.GetProperty("projectedMonthEndBalance").GetDecimal());
    }

    private static async Task<decimal> CoupleTotalAsync(HttpClient client, string month)
    {
        var income = await client.GetFromJsonAsync<JsonElement>($"/api/v1/incomes/{month}");
        return income.GetProperty("coupleTotal").GetDecimal();
    }

    private static async Task AddIncomeAsync(HttpClient client, string month, string name, decimal amount, bool recurring)
    {
        var response = await client.PostAsJsonAsync("/api/v1/incomes", new
        {
            month,
            name,
            amount,
            currency = "BRL",
            isShared = false,
            isRecurring = recurring
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task RegisterWithCoupleAsync(HttpClient client)
    {
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = $"rec-income-{Guid.NewGuid():N}@example.com",
            Name = "Ana",
            Password = "SecurePass123!"
        });
        register.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);

        var created = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);
    }
}
