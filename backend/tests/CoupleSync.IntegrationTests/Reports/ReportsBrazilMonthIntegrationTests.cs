using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.IntegrationTests.Transactions;

namespace CoupleSync.IntegrationTests.Reports;

/// <summary>DAD-04: reports group transactions by the Brasília month, not the UTC one.</summary>
[Trait("Category", "BrazilMonth")]
public sealed class ReportsBrazilMonthIntegrationTests
{
    [Fact]
    public async Task MonthlyTrends_PutsTheLastNightOfAMonthInThatMonth()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = $"brt-{Guid.NewGuid():N}@example.com",
            Name = "Ana",
            Password = "SecurePass123!"
        });
        register.EnsureSuccessStatusCode();
        UseToken(client, (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);
        var couple = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, couple.StatusCode);
        UseToken(client, (await couple.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);

        // 23:30 of the previous month's last day in Brasília: already the 1st in UTC.
        var current = BrazilTime.MonthOf(DateTime.UtcNow);
        var previous = BrazilTime.AddMonths(current, -1);
        var lateNight = BrazilTime.MonthRangeUtc(current).StartUtc.AddMinutes(-30);
        Assert.Equal(current, lateNight.ToString("yyyy-MM")); // the UTC month would be the wrong one

        var ingest = await client.PostAsJsonAsync("/api/v1/integrations/events", new
        {
            Bank = "NUBANK",
            Amount = 80m,
            Currency = "BRL",
            EventTimestamp = lateNight,
            Description = "Jantar",
            Merchant = "Restaurante",
            RawNotificationText = "Nubank: R$80,00 em Restaurante"
        });
        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);

        var trends = await client.GetFromJsonAsync<JsonElement>("/api/v1/reports/monthly-trends?months=2");
        var expenses = trends.GetProperty("months").EnumerateArray()
            .ToDictionary(m => m.GetProperty("month").GetString()!, m => m.GetProperty("expense").GetDecimal());

        Assert.Equal(80m, expenses[previous]);
        Assert.Equal(0m, expenses[current]);
    }

    private static void UseToken(HttpClient client, string token)
        => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}
