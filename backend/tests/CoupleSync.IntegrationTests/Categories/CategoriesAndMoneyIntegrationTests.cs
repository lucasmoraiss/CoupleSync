using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CoupleSync.IntegrationTests.Transactions;

namespace CoupleSync.IntegrationTests.Categories;

/// <summary>
/// Canonical categories, BRL-only currency and the single amount rule, seen from the HTTP edge. The
/// payloads are exactly what the installed app (v1.0.0-pit) sends: free-text/accented categories and "BRL".
/// </summary>
[Trait("Category", "CanonicalCategories")]
public sealed class CategoriesAndMoneyIntegrationTests
{
    private const string Password = "SecurePass123!";

    // ── GET /api/v1/categories ───────────────────────────────────────────

    [Fact]
    public async Task GetCategories_WithoutToken_Returns401()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/categories");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetCategories_Authenticated_ReturnsTheCanonicalList()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await RegisterAsync(client);

        var response = await client.GetAsync("/api/v1/categories");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var categories = body.GetProperty("categories").EnumerateArray()
            .Select(c => (c.GetProperty("key").GetString(), c.GetProperty("label").GetString()))
            .ToList();
        Assert.Equal(
            new (string?, string?)[]
            {
                ("ALIMENTACAO", "Alimentação"), ("TRANSPORTE", "Transporte"), ("COMPRAS", "Compras"),
                ("SAUDE", "Saúde"), ("LAZER", "Lazer"), ("MORADIA", "Moradia"), ("OUTROS", "Outros"),
            },
            categories);
    }

    // ── POST /api/v1/transactions ────────────────────────────────────────

    [Theory]
    [InlineData("Alimentação", "ALIMENTACAO")]
    [InlineData("alimentacao", "ALIMENTACAO")]
    [InlineData("  ALIMENTAÇÃO ", "ALIMENTACAO")]
    [InlineData("saúde", "SAUDE")]
    [InlineData("Outros", "OUTROS")]
    public async Task ManualTransaction_AnySpellingOfACategory_IsStoredAsTheKey(string sent, string stored)
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var response = await PostJsonAsync(client, "/api/v1/transactions",
            $$"""{"amount":42.5,"currency":"BRL","description":"x","category":"{{sent}}"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(stored, body.GetProperty("category").GetString());
        Assert.Equal("BRL", body.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task ManualTransaction_UnknownCategory_Returns400WithTheAcceptedList()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var response = await PostJsonAsync(client, "/api/v1/transactions",
            """{"amount":10,"currency":"BRL","category":"Mercado"}""");

        var body = await AssertValidationErrorAsync(response);
        var message = body.GetProperty("errors").GetProperty("Category")[0].GetString()!;
        Assert.StartsWith("Categoria inválida", message);
        Assert.Contains("ALIMENTACAO", message);
        Assert.Contains("OUTROS", message);
    }

    [Fact]
    public async Task ManualTransaction_AbsentCurrency_AssumesBrl()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var response = await PostJsonAsync(client, "/api/v1/transactions", """{"amount":10,"category":"Lazer"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BRL", body.GetProperty("currency").GetString());
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    [InlineData("R$")]
    public async Task ManualTransaction_OtherCurrency_Returns400(string currency)
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var response = await PostJsonAsync(client, "/api/v1/transactions",
            $$"""{"amount":10,"currency":"{{currency}}","category":"Lazer"}""");

        var body = await AssertValidationErrorAsync(response);
        Assert.Equal("Só é aceita a moeda BRL (real).", body.GetProperty("errors").GetProperty("Currency")[0].GetString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("10.001")]
    [InlineData("10.555")]
    [InlineData("1000000000")]
    [InlineData("999999999.995")]
    public async Task ManualTransaction_BadAmount_Returns400(string amount)
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var response = await PostJsonAsync(client, "/api/v1/transactions",
            $$"""{"amount":{{amount}},"currency":"BRL","category":"Lazer"}""");

        var body = await AssertValidationErrorAsync(response);
        Assert.True(body.GetProperty("errors").TryGetProperty("Amount", out _));
    }

    [Theory]
    [InlineData("0.01")]
    [InlineData("10.5")]
    [InlineData("999999999.99")]
    public async Task ManualTransaction_BoundaryAmounts_AreAccepted(string amount)
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var response = await PostJsonAsync(client, "/api/v1/transactions",
            $$"""{"amount":{{amount}},"currency":"BRL","category":"Lazer"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ── PATCH category and the category filter ───────────────────────────

    [Fact]
    public async Task PatchCategory_AcceptsLabelsAndRejectsUnknown_AndFilterAcceptsAnySpelling()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var created = await PostJsonAsync(client, "/api/v1/transactions", """{"amount":10,"currency":"BRL","category":"Lazer"}""");
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var ok = await client.PatchAsJsonAsync($"/api/v1/transactions/{id}/category", new { Category = "Saúde" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("SAUDE", (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("category").GetString());

        var bad = await client.PatchAsJsonAsync($"/api/v1/transactions/{id}/category", new { Category = "Educação" });
        var body = await AssertValidationErrorAsync(bad);
        Assert.Contains("ALIMENTACAO", body.GetProperty("errors").GetProperty("Category")[0].GetString());

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/transactions?category=sa%C3%BAde");
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
    }

    // ── POST /api/v1/integrations/events ─────────────────────────────────

    [Fact]
    public async Task IngestEvent_InstalledAppPayload_StoresACanonicalCategory()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);
        var timestamp = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

        // The app sends bank, amount, currency, timestamp and merchant; the category comes from the rules.
        var response = await PostJsonAsync(client, "/api/v1/integrations/events",
            $$"""{"bank":"Nubank","amount":31.9,"currency":"BRL","eventTimestamp":"{{timestamp}}","merchant":"Padaria do Zé"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/transactions");
        var item = list.GetProperty("items")[0];
        Assert.Equal("BRL", item.GetProperty("currency").GetString());
        Assert.Contains(item.GetProperty("category").GetString(),
            new[] { "ALIMENTACAO", "TRANSPORTE", "COMPRAS", "SAUDE", "LAZER", "MORADIA", "OUTROS" });
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    public async Task IngestEvent_OtherCurrency_Returns400(string currency)
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);
        var timestamp = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

        var response = await PostJsonAsync(client, "/api/v1/integrations/events",
            $$"""{"bank":"Nubank","amount":31.9,"currency":"{{currency}}","eventTimestamp":"{{timestamp}}"}""");

        await AssertValidationErrorAsync(response);
    }

    // ── budgets, goals, incomes ──────────────────────────────────────────

    [Fact]
    public async Task Budget_AllocationsAreNormalized_AndBadMoneyIsRejected()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var usd = await PostJsonAsync(client, "/api/v1/budgets", """{"month":"2026-10","grossIncome":5000,"currency":"USD"}""");
        await AssertValidationErrorAsync(usd);
        var zero = await PostJsonAsync(client, "/api/v1/budgets", """{"month":"2026-10","grossIncome":0,"currency":"BRL"}""");
        await AssertValidationErrorAsync(zero);
        var threeDecimals = await PostJsonAsync(client, "/api/v1/budgets", """{"month":"2026-10","grossIncome":5000.123,"currency":"BRL"}""");
        await AssertValidationErrorAsync(threeDecimals);

        var plan = await PostJsonAsync(client, "/api/v1/budgets", """{"month":"2026-10","grossIncome":5000}""");
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        var planBody = await plan.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BRL", planBody.GetProperty("currency").GetString());
        var planId = planBody.GetProperty("id").GetGuid();

        var put = await client.PutAsync($"/api/v1/budgets/{planId}/allocations", new StringContent(
            """{"allocations":[{"category":"Alimentação","allocatedAmount":1000,"currency":"BRL"},{"category":"saude","allocatedAmount":300.5,"currency":"BRL"}]}""",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var allocations = (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("allocations").EnumerateArray()
            .Select(a => a.GetProperty("category").GetString()).ToList();
        Assert.Equal(new[] { "ALIMENTACAO", "SAUDE" }, allocations);

        var unknown = await client.PutAsync($"/api/v1/budgets/{planId}/allocations", new StringContent(
            """{"allocations":[{"category":"Mercado","allocatedAmount":10,"currency":"BRL"}]}""",
            Encoding.UTF8, "application/json"));
        await AssertValidationErrorAsync(unknown);
    }

    [Fact]
    public async Task Goal_OtherCurrencyOrThreeDecimals_Returns400_AndAbsentCurrencyIsBrl()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);
        var deadline = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

        var eur = await PostJsonAsync(client, "/api/v1/goals", $$"""{"title":"Viagem","targetAmount":1000,"currency":"EUR","deadline":"{{deadline}}"}""");
        await AssertValidationErrorAsync(eur);
        var decimals = await PostJsonAsync(client, "/api/v1/goals", $$"""{"title":"Viagem","targetAmount":1000.005,"currency":"BRL","deadline":"{{deadline}}"}""");
        await AssertValidationErrorAsync(decimals);
        var tooBig = await PostJsonAsync(client, "/api/v1/goals", $$"""{"title":"Viagem","targetAmount":1000000000,"currency":"BRL","deadline":"{{deadline}}"}""");
        await AssertValidationErrorAsync(tooBig);

        var ok = await PostJsonAsync(client, "/api/v1/goals", $$"""{"title":"Viagem","targetAmount":1000,"deadline":"{{deadline}}"}""");
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal("BRL", (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Income_ZeroOtherCurrencyOrThreeDecimals_Returns400()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        foreach (var payload in new[]
        {
            """{"month":"2026-10","name":"Salário","amount":0,"currency":"BRL","isShared":false}""",
            """{"month":"2026-10","name":"Salário","amount":100,"currency":"USD","isShared":false}""",
            """{"month":"2026-10","name":"Salário","amount":100.123,"currency":"BRL","isShared":false}""",
            """{"month":"2026-10","name":"Salário","amount":1000000000,"currency":"BRL","isShared":false}""",
        })
        {
            await AssertValidationErrorAsync(await PostJsonAsync(client, "/api/v1/incomes", payload));
        }

        var ok = await PostJsonAsync(client, "/api/v1/incomes", """{"month":"2026-10","name":"Salário","amount":5000.5,"isShared":false}""");
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static async Task<JsonElement> AssertValidationErrorAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400, got {(int)response.StatusCode}: {text}");
        var body = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
        return body;
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, string json)
        => client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task RegisterAsync(HttpClient client, string? email = null)
    {
        email ??= $"cat-{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { Email = email, Name = "Test User", Password });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.GetProperty("accessToken").GetString());
    }

    private static async Task AuthenticateWithCoupleAsync(HttpClient client)
    {
        var email = $"cat-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(client, email);

        var couple = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, couple.StatusCode);

        client.DefaultRequestHeaders.Authorization = null;
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password });
        login.EnsureSuccessStatusCode();
        var loggedIn = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", loggedIn.GetProperty("accessToken").GetString());
    }
}
