using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.IntegrationTests.Transactions;

namespace CoupleSync.IntegrationTests.Income;

/// <summary>A13 — GET /api/v1/incomes/current in a group with three members.</summary>
[Trait("Category", "Income")]
public sealed class IncomeGroupIntegrationTests
{
    [Fact]
    public async Task CurrentIncome_GroupOfThree_EveryMemberSeesTheSameTotalAndAllPartners()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var clientA = factory.CreateClient();
        using var clientB = factory.CreateClient();
        using var clientD = factory.CreateClient();

        await RegisterAsync(clientA, "Ana");
        var created = await clientA.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var couple = await created.Content.ReadFromJsonAsync<JsonElement>();
        UseToken(clientA, couple.GetProperty("accessToken").GetString()!);
        var joinCode = couple.GetProperty("joinCode").GetString()!;

        await RegisterAsync(clientB, "Bruno");
        await JoinAsync(clientB, joinCode);
        await RegisterAsync(clientD, "Dani");
        await JoinAsync(clientD, joinCode);

        await AddIncomeAsync(clientA, "Salário A", 5000m, isShared: false);
        await AddIncomeAsync(clientB, "Salário B", 3000m, isShared: false);
        await AddIncomeAsync(clientD, "Salário D", 2000m, isShared: false);
        await AddIncomeAsync(clientB, "Aluguel", 1000m, isShared: true);

        foreach (var (name, client) in new[] { ("Ana", clientA), ("Bruno", clientB), ("Dani", clientD) })
        {
            var income = await client.GetFromJsonAsync<JsonElement>("/api/v1/incomes/current");

            Assert.True(11000m == income.GetProperty("coupleTotal").GetDecimal(),
                $"{name}: coupleTotal = {income.GetProperty("coupleTotal").GetDecimal()}");
            Assert.Equal(2, income.GetProperty("partnersIncome").GetArrayLength());
            Assert.Equal(1000m, income.GetProperty("sharedIncome").GetProperty("total").GetDecimal());

            // Existing field used by the app: personal + partner + shared add up to the total.
            var sum = income.GetProperty("personalIncome").GetProperty("total").GetDecimal()
                + income.GetProperty("partnerIncome").GetProperty("total").GetDecimal()
                + income.GetProperty("sharedIncome").GetProperty("total").GetDecimal();
            Assert.Equal(11000m, sum);
        }

        var forDani = await clientD.GetFromJsonAsync<JsonElement>("/api/v1/incomes/current");
        var partnerNames = forDani.GetProperty("partnersIncome").EnumerateArray()
            .Select(g => g.GetProperty("userName").GetString())
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(new[] { "Ana", "Bruno" }, partnerNames);
        Assert.Equal(2000m, forDani.GetProperty("personalIncome").GetProperty("total").GetDecimal());
    }

    private static async Task RegisterAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = $"a13-{Guid.NewGuid():N}@example.com",
            Name = name,
            Password = "SecurePass123!"
        });
        response.EnsureSuccessStatusCode();
        UseToken(client, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);
    }

    private static async Task JoinAsync(HttpClient client, string joinCode)
    {
        var response = await client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = joinCode });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        UseToken(client, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);
    }

    private static async Task AddIncomeAsync(HttpClient client, string name, decimal amount, bool isShared)
    {
        var response = await client.PostAsJsonAsync("/api/v1/incomes", new
        {
            month = DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            name,
            amount,
            currency = "BRL",
            isShared
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static void UseToken(HttpClient client, string token)
        => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}
