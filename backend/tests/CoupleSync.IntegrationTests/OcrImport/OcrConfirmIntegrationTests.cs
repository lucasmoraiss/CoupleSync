using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.OcrImport;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleSync.IntegrationTests.OcrImport;

/// <summary>
/// A02 + A14 (and A04) — POST /api/v1/ocr/{uploadId}/confirm against the real repositories
/// (SQLite), including the unique index on (couple_id, fingerprint).
/// </summary>
[Trait("Category", "Ocr")]
public sealed class OcrConfirmIntegrationTests
{
    private static readonly DateTime StatementDay = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Confirm_StatementWithTwoIdenticalLines_StoresBothTransactions()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();
        var coupleId = await AuthenticateWithCoupleAsync(client);

        var fingerprint = OcrProcessingService.ComputeFingerprint(coupleId, StatementDay, 12.90m, "Corrida App");
        var uploadId = await UploadAndMarkReadyAsync(factory, client,
        [
            Candidate(0, "Corrida App", 12.90m, fingerprint),
            Candidate(1, "Corrida App", 12.90m, fingerprint)
        ]);

        var response = await client.PostAsJsonAsync($"/api/v1/ocr/{uploadId}/confirm", new { selectedIndices = new[] { 0, 1 } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("transactionsCreated").GetInt32());
        Assert.Equal(0, body.GetProperty("duplicatesSkipped").GetInt32());

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/transactions");
        Assert.Equal(2, list.GetProperty("totalCount").GetInt32());
        Assert.All(list.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal("Corrida App", item.GetProperty("description").GetString());
            Assert.Equal(12.90m, item.GetProperty("amount").GetDecimal());
        });
    }

    [Fact]
    public async Task Confirm_ReimportedStatement_SkipsDuplicatesAndClosesTheJob()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        OcrCandidate[] statement =
        [
            Candidate(0, "Mercado", 80m, "fp-mercado"),
            Candidate(1, "Farmácia", 35m, "fp-farmacia")
        ];

        var firstUpload = await UploadAndMarkReadyAsync(factory, client, statement);
        var first = await client.PostAsJsonAsync($"/api/v1/ocr/{firstUpload}/confirm", new { selectedIndices = new[] { 0, 1 } });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var secondUpload = await UploadAndMarkReadyAsync(factory, client, statement);
        var second = await client.PostAsJsonAsync($"/api/v1/ocr/{secondUpload}/confirm", new { selectedIndices = new[] { 0, 1 } });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("transactionsCreated").GetInt32());
        Assert.Equal(2, body.GetProperty("duplicatesSkipped").GetInt32());

        var status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/ocr/{secondUpload}/status");
        Assert.Equal("Confirmed", status.GetProperty("status").GetString());

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/transactions");
        Assert.Equal(2, list.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    public async Task Confirm_IndexThatDoesNotExist_Returns422_AndKeepsTheJobReady(int badIndex)
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var uploadId = await UploadAndMarkReadyAsync(factory, client,
        [
            Candidate(0, "Mercado", 80m, "fp-mercado"),
            Candidate(1, "Farmácia", 35m, "fp-farmacia")
        ]);

        var rejected = await client.PostAsJsonAsync($"/api/v1/ocr/{uploadId}/confirm", new { selectedIndices = new[] { badIndex } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        var error = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_SELECTION", error.GetProperty("code").GetString());

        var status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/ocr/{uploadId}/status");
        Assert.Equal("Ready", status.GetProperty("status").GetString());

        // The candidates are still reachable and can be confirmed with a valid selection.
        var accepted = await client.PostAsJsonAsync($"/api/v1/ocr/{uploadId}/confirm", new { selectedIndices = new[] { 0, 1 } });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(2, (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transactionsCreated").GetInt32());
    }

    // ── A04: candidateEdits ────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_WithCandidateEdits_StoresTheEditedDescriptionAndAmount()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        OcrCandidate[] statement =
        [
            Candidate(0, "MERC*0001 SAO PAULO", 80m, "fp-mercado"),
            Candidate(1, "FARM 22", 35m, "fp-farmacia"),
            Candidate(2, "PADARIA", 18.5m, "fp-padaria")
        ];
        var uploadId = await UploadAndMarkReadyAsync(factory, client, statement);

        // Exactly the body the mobile app sends (camelCase, only the changed fields).
        var response = await PostJsonAsync(client, $"/api/v1/ocr/{uploadId}/confirm",
            """{"selectedIndices":[0,1,2],"categoryOverrides":[{"index":0,"category":"Alimentação"}],"candidateEdits":[{"index":0,"description":"Mercado do bairro","amount":150},{"index":1,"amount":25.9}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transactionsCreated").GetInt32());

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/transactions");
        var items = list.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("description").GetString()!, i => i);

        Assert.Equal(3, items.Count);
        Assert.Equal(150m, items["Mercado do bairro"].GetProperty("amount").GetDecimal());
        Assert.Equal("ALIMENTACAO", items["Mercado do bairro"].GetProperty("category").GetString());
        Assert.Equal(25.9m, items["FARM 22"].GetProperty("amount").GetDecimal());
        Assert.Equal(18.5m, items["PADARIA"].GetProperty("amount").GetDecimal());

        // Re-importing the same file is still recognised, even for the edited lines.
        var secondUpload = await UploadAndMarkReadyAsync(factory, client, statement);
        var second = await client.PostAsJsonAsync($"/api/v1/ocr/{secondUpload}/confirm", new { selectedIndices = new[] { 0, 1, 2 } });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, secondBody.GetProperty("transactionsCreated").GetInt32());
        Assert.Equal(3, secondBody.GetProperty("duplicatesSkipped").GetInt32());
    }

    [Fact]
    public async Task Confirm_WithInvalidCandidateEdits_Returns400_AndKeepsTheJobReady()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var uploadId = await UploadAndMarkReadyAsync(factory, client,
        [
            Candidate(0, "Mercado", 80m, "fp-mercado"),
            Candidate(1, "Farmácia", 35m, "fp-farmacia")
        ]);

        var invalidBodies = new Dictionary<string, string>
        {
            ["blank description"] = """{"selectedIndices":[0],"candidateEdits":[{"index":0,"description":"   "}]}""",
            ["description 513"] = $$"""{"selectedIndices":[0],"candidateEdits":[{"index":0,"description":"{{new string('d', 513)}}"}]}""",
            ["amount zero"] = """{"selectedIndices":[0],"candidateEdits":[{"index":0,"amount":0}]}""",
            ["amount negative"] = """{"selectedIndices":[0],"candidateEdits":[{"index":0,"amount":-10}]}""",
            ["amount 3 decimals"] = """{"selectedIndices":[0],"candidateEdits":[{"index":0,"amount":10.005}]}""",
            ["amount above ceiling"] = """{"selectedIndices":[0],"candidateEdits":[{"index":0,"amount":1e20}]}""",
            ["repeated index"] = """{"selectedIndices":[0],"candidateEdits":[{"index":0,"amount":10},{"index":0,"description":"X"}]}""",
            ["index not selected"] = """{"selectedIndices":[0],"candidateEdits":[{"index":1,"amount":10}]}"""
        };

        foreach (var (name, json) in invalidBodies)
        {
            var response = await PostJsonAsync(client, $"/api/v1/ocr/{uploadId}/confirm", json);
            Assert.True(HttpStatusCode.BadRequest == response.StatusCode, $"{name}: expected 400, got {(int)response.StatusCode}");
        }

        var status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/ocr/{uploadId}/status");
        Assert.Equal("Ready", status.GetProperty("status").GetString());
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/transactions");
        Assert.Equal(0, list.GetProperty("totalCount").GetInt32());
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, string json)
        => client.PostAsync(url, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

    internal static OcrCandidate Candidate(int index, string description, decimal amount, string fingerprint)
        => new()
        {
            Index = index,
            Date = StatementDay,
            Description = description,
            Amount = amount,
            Currency = "BRL",
            Confidence = 1.0,
            Fingerprint = fingerprint
        };

    internal static async Task<Guid> AuthenticateWithCoupleAsync(HttpClient client)
    {
        const string password = "SecurePass123!";
        var email = $"ocr-confirm-{Guid.NewGuid():N}@example.com";

        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new { Email = email, Name = "Test User", Password = password });
        register.EnsureSuccessStatusCode();
        var registered = await register.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registered.GetProperty("accessToken").GetString());

        var couple = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, couple.StatusCode);
        var coupleId = (await couple.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("coupleId").GetGuid();

        client.DefaultRequestHeaders.Authorization = null;
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password = password });
        login.EnsureSuccessStatusCode();
        var loggedIn = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", loggedIn.GetProperty("accessToken").GetString());

        return coupleId;
    }

    internal static async Task<Guid> UploadAndMarkReadyAsync(
        OcrWebApplicationFactory factory, HttpClient client, IReadOnlyList<OcrCandidate> candidates)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", "statement.jpg");

        var upload = await client.PostAsync("/api/v1/ocr/upload", content);
        upload.EnsureSuccessStatusCode();
        var uploadId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetGuid();

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
}
