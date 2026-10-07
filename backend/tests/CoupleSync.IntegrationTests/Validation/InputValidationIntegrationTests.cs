using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CoupleSync.Application.OcrImport;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.IntegrationTests.OcrImport;
using CoupleSync.IntegrationTests.Transactions;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleSync.IntegrationTests.Validation;

/// <summary>
/// A07 — invalid input must be answered with 400, never with 500.
/// The cases that only blow up on PostgreSQL (column length / numeric overflow / timestamp kind)
/// are asserted here as "rejected at the edge", which is what makes them safe on any provider.
/// </summary>
[Trait("Category", "InputValidation")]
public sealed class InputValidationIntegrationTests
{
    private const string OneE20 = "99999999999999999999";

    // ── POST /api/v1/transactions ──────────────────────────────────────────

    [Fact]
    public async Task CreateManualTransaction_WithOffsetTimestamp_IsConvertedToUtc()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var response = await PostJsonAsync(client, "/api/v1/transactions",
            """{"amount":42.5,"currency":"BRL","eventTimestampUtc":"2026-10-03T10:00:00-03:00","description":"Mercado","category":"Alimentação"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("2026-10-03T13:00:00Z", body.RootElement.GetProperty("eventTimestampUtc").GetString());
    }

    [Fact]
    public async Task CreateManualTransaction_WithInvalidFields_Returns400()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var invalidBodies = new Dictionary<string, string>
        {
            ["description 513"] = ManualTransactionJson(description: new string('d', 513)),
            ["description 5000"] = ManualTransactionJson(description: new string('d', 5000)),
            ["merchant 5000"] = ManualTransactionJson(merchant: new string('m', 5000)),
            ["category 65"] = ManualTransactionJson(category: new string('c', 65)),
            ["currency REAISREAIS"] = ManualTransactionJson(currency: "REAISREAIS"),
            ["amount 1e20"] = ManualTransactionJson(amount: OneE20)
        };

        foreach (var (name, json) in invalidBodies)
        {
            var response = await PostJsonAsync(client, "/api/v1/transactions", json);
            Assert.True(HttpStatusCode.BadRequest == response.StatusCode, $"{name}: expected 400, got {(int)response.StatusCode}");
        }

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/transactions");
        Assert.Equal(0, list.GetProperty("totalCount").GetInt32());
    }

    // ── POST /api/v1/integrations/events ───────────────────────────────────

    [Fact]
    public async Task IngestEvent_WithMissingFields_Returns400()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var timestamp = DateTime.UtcNow.AddHours(-2).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        // An absent currency means BRL (the installed app always sends it, but older clients may not).
        var withoutCurrency = await PostJsonAsync(client, "/api/v1/integrations/events",
            $$"""{"bank":"NUBANK","amount":10.5,"eventTimestamp":"{{timestamp}}"}""");
        Assert.Equal(HttpStatusCode.Created, withoutCurrency.StatusCode);

        var emptyBody = await PostJsonAsync(client, "/api/v1/integrations/events", "{}");
        Assert.Equal(HttpStatusCode.BadRequest, emptyBody.StatusCode);
    }

    [Fact]
    public async Task IngestEvent_WithNonUtcTimestamps_IsAccepted()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var reference = DateTime.UtcNow.AddHours(-6);
        var withoutZone = reference.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var withOffset = new DateTimeOffset(reference).ToOffset(TimeSpan.FromHours(-3))
            .ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

        var first = await PostJsonAsync(client, "/api/v1/integrations/events",
            $$"""{"bank":"NUBANK","amount":10.5,"currency":"BRL","eventTimestamp":"{{withoutZone}}","merchant":"Loja A"}""");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await PostJsonAsync(client, "/api/v1/integrations/events",
            $$"""{"bank":"NUBANK","amount":20.5,"currency":"BRL","eventTimestamp":"{{withOffset}}","merchant":"Loja B"}""");
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        // Both spellings denote the same instant, so both transactions carry the same UTC timestamp.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = db.Transactions.Select(t => t.EventTimestampUtc).ToList();
        Assert.Equal(2, stored.Count);
        Assert.Equal(stored[0], stored[1]);
        Assert.Equal(new DateTime(reference.Year, reference.Month, reference.Day, reference.Hour, reference.Minute, reference.Second), stored[0]);
    }

    // ── GET /api/v1/transactions ───────────────────────────────────────────

    [Fact]
    public async Task GetTransactions_WithPageThatOverflowsOffset_Returns400()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var created = await PostJsonAsync(client, "/api/v1/transactions", ManualTransactionJson());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var response = await client.GetAsync("/api/v1/transactions?page=2147483647&pageSize=100");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Equal("VALIDATION_ERROR", error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
    }

    [Fact]
    public async Task GetTransactions_WithPageBeyondData_ReturnsEmptyPage()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var created = await PostJsonAsync(client, "/api/v1/transactions", ManualTransactionJson());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/transactions?page=1000&pageSize=100");

        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, page.GetProperty("items").GetArrayLength());
    }

    // ── /api/v1/incomes ────────────────────────────────────────────────────

    [Fact]
    public async Task Incomes_WithBlankNameOrHugeAmount_Return400()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var month = CoupleSync.Domain.ValueObjects.BrazilTime.MonthOf(DateTime.UtcNow);
        var create = await PostJsonAsync(client, "/api/v1/incomes",
            $$"""{"month":"{{month}}","name":"Salário","amount":5000,"currency":"BRL","isShared":false}""");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var emptyName = await SendJsonAsync(client, HttpMethod.Put, $"/api/v1/incomes/{id}", """{"name":""}""");
        Assert.Equal(HttpStatusCode.BadRequest, emptyName.StatusCode);

        var blankName = await SendJsonAsync(client, HttpMethod.Put, $"/api/v1/incomes/{id}", """{"name":"   "}""");
        Assert.Equal(HttpStatusCode.BadRequest, blankName.StatusCode);

        var hugeUpdate = await SendJsonAsync(client, HttpMethod.Put, $"/api/v1/incomes/{id}", """{"amount":1e20}""");
        Assert.Equal(HttpStatusCode.BadRequest, hugeUpdate.StatusCode);

        var hugeCreate = await PostJsonAsync(client, "/api/v1/incomes",
            $$"""{"month":"{{month}}","name":"Bônus","amount":1e20,"currency":"BRL","isShared":false}""");
        Assert.Equal(HttpStatusCode.BadRequest, hugeCreate.StatusCode);
    }

    // ── /api/v1/budgets ────────────────────────────────────────────────────

    [Fact]
    public async Task Budgets_WithHugeAmounts_Return400()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var income = await SendJsonAsync(client, HttpMethod.Patch, "/api/v1/budgets/income", """{"grossIncome":1e20}""");
        Assert.Equal(HttpStatusCode.BadRequest, income.StatusCode);

        var month = CoupleSync.Domain.ValueObjects.BrazilTime.MonthOf(DateTime.UtcNow);
        var plan = await PostJsonAsync(client, "/api/v1/budgets",
            $$"""{"month":"{{month}}","grossIncome":8000,"currency":"BRL"}""");
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        var planId = (await plan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var allocations = await SendJsonAsync(client, HttpMethod.Put, $"/api/v1/budgets/{planId}/allocations",
            """{"allocations":[{"category":"Moradia","allocatedAmount":1e20,"currency":"BRL"}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, allocations.StatusCode);
    }

    // ── /api/v1/goals ──────────────────────────────────────────────────────

    [Fact]
    public async Task Goals_WithOutOfRangeAmounts_Return400()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var deadline = DateTime.UtcNow.AddMonths(6).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        foreach (var target in new[] { "1e16", "1e18", "0.001" })
        {
            var rejected = await PostJsonAsync(client, "/api/v1/goals",
                $$"""{"title":"Meta {{target}}","targetAmount":{{target}},"currency":"BRL","deadline":"{{deadline}}"}""");
            Assert.True(HttpStatusCode.BadRequest == rejected.StatusCode, $"create targetAmount {target}: got {(int)rejected.StatusCode}");
        }

        var create = await PostJsonAsync(client, "/api/v1/goals",
            $$"""{"title":"Viagem","targetAmount":10000,"currency":"BRL","deadline":"{{deadline}}"}""");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var patches = new Dictionary<string, string>
        {
            ["negative currentAmount + title"] = """{"title":"Viagem 2","currentAmount":-5}""",
            ["negative currentAmount + deadline"] = $$"""{"deadline":"{{deadline}}","currentAmount":-5}""",
            ["targetAmount 1e16"] = """{"targetAmount":1e16}""",
            ["targetAmount 1e18"] = """{"targetAmount":1e18}""",
            ["currentAmount 1e18"] = """{"title":"Viagem 3","currentAmount":1e18}"""
        };

        foreach (var (name, json) in patches)
        {
            var response = await SendJsonAsync(client, HttpMethod.Patch, $"/api/v1/goals/{id}", json);
            Assert.True(HttpStatusCode.BadRequest == response.StatusCode, $"{name}: expected 400, got {(int)response.StatusCode}");
        }

        var goal = await client.GetFromJsonAsync<JsonElement>($"/api/v1/goals/{id}");
        Assert.Equal("Viagem", goal.GetProperty("title").GetString());
        Assert.Equal(10000m, goal.GetProperty("targetAmount").GetDecimal());

        var progress = await client.GetAsync($"/api/v1/goals/{id}/progress");
        Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
    }

    // ── GET /api/v1/dashboard ──────────────────────────────────────────────

    [Fact]
    public async Task Dashboard_WithSingleBoundThatInvertsThePeriod_Returns400()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var now = DateTime.UtcNow;
        var futureStart = new DateTime(now.Year, now.Month, 1).AddMonths(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var pastEnd = new DateTime(now.Year, now.Month, 1).AddMonths(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var onlyFutureStart = await client.GetAsync($"/api/v1/dashboard?startDate={futureStart}");
        Assert.Equal(HttpStatusCode.BadRequest, onlyFutureStart.StatusCode);
        var error = await onlyFutureStart.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Equal("INVALID_DATE_RANGE", error!.Code);
        Assert.Contains("data inicial", error.Message);

        var onlyPastEnd = await client.GetAsync($"/api/v1/dashboard?endDate={pastEnd}");
        Assert.Equal(HttpStatusCode.BadRequest, onlyPastEnd.StatusCode);
    }

    // ── POST /api/v1/ocr/{uploadId}/confirm ────────────────────────────────

    [Fact]
    public async Task OcrConfirm_WithInvalidCategoryOverrides_Returns400()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var uploadId = await UploadAndMarkReadyAsync(factory, client, candidateCount: 2);

        var repeatedIndex = await PostJsonAsync(client, $"/api/v1/ocr/{uploadId}/confirm",
            """{"selectedIndices":[0,1],"categoryOverrides":[{"index":0,"category":"Lazer"},{"index":0,"category":"Mercado"}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, repeatedIndex.StatusCode);

        var longCategory = await PostJsonAsync(client, $"/api/v1/ocr/{uploadId}/confirm",
            $$"""{"selectedIndices":[0],"categoryOverrides":[{"index":0,"category":"{{new string('c', 65)}}"}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, longCategory.StatusCode);

        // Nothing was consumed: the job is still Ready and can be confirmed normally.
        var status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/ocr/{uploadId}/status");
        Assert.Equal("Ready", status.GetProperty("status").GetString());
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string ManualTransactionJson(
        string amount = "25.90",
        string currency = "BRL",
        string description = "Almoço",
        string merchant = "Padaria",
        string category = "Alimentação")
        => $$"""{"amount":{{amount}},"currency":"{{currency}}","eventTimestampUtc":"2026-10-01T12:00:00Z","description":"{{description}}","merchant":"{{merchant}}","category":"{{category}}"}""";

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, string json)
        => SendJsonAsync(client, HttpMethod.Post, url, json);

    private static Task<HttpResponseMessage> SendJsonAsync(HttpClient client, HttpMethod method, string url, string json)
        => client.SendAsync(new HttpRequestMessage(method, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

    private static async Task AuthenticateWithCoupleAsync(HttpClient client)
    {
        const string password = "SecurePass123!";
        var email = $"a07-{Guid.NewGuid():N}@example.com";

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

    private static async Task<Guid> UploadAndMarkReadyAsync(OcrWebApplicationFactory factory, HttpClient client, int candidateCount)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", "receipt.jpg");

        var upload = await client.PostAsync("/api/v1/ocr/upload", content);
        upload.EnsureSuccessStatusCode();
        var uploadId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetGuid();

        var candidates = Enumerable.Range(0, candidateCount)
            .Select(i => new OcrCandidate
            {
                Index = i,
                Date = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc),
                Description = $"Item {i + 1}",
                Amount = 10m * (i + 1),
                Currency = "BRL",
                Confidence = 0.95,
                Fingerprint = $"fp{i:D4}"
            })
            .ToList();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.ImportJobs.FindAsync(uploadId);
        Assert.NotNull(job);
        var now = DateTime.UtcNow;
        job!.MarkProcessing(now);
        job.MarkReady(JsonSerializer.Serialize(candidates), now);
        await db.SaveChangesAsync();

        return uploadId;
    }

    private sealed record ErrorDto(string Code, string Message, string TraceId);
}
