using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.OcrImport;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleSync.IntegrationTests.OcrImport;

/// <summary>
/// Partial confirmation (S5 5.33), credits shown but never imported (S5 5.58) and the job stuck in
/// Processing (DAD-05), against the real repositories (SQLite). Data is invented.
/// </summary>
[Trait("Category", "Ocr")]
public sealed class OcrImportLifecycleIntegrationTests
{
    [Fact]
    public async Task Results_ListCreditsApartFromTheDebitLines_WithTheirCount()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();
        await OcrConfirmIntegrationTests.AuthenticateWithCoupleAsync(client);
        var uploadId = await OcrConfirmIntegrationTests.UploadAndMarkReadyAsync(factory, client,
        [
            Debit(0, "Mercado", 80m),
            Debit(1, "Farmácia", 35m),
            Credit(2, "Reembolso Empresa Ficticia", 500m),
            Credit(3, "Estorno Loja Aurora", 30m),
            Credit(4, "Deposito Tia Fulana", 300m)
        ]);

        var results = await client.GetFromJsonAsync<JsonElement>($"/api/v1/ocr/{uploadId}/results");

        var candidates = results.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, c => Assert.Equal("Pending", c.GetProperty("lineState").GetString()));
        Assert.Equal(3, results.GetProperty("creditsCount").GetInt32());
        Assert.Equal(3, results.GetProperty("credits").GetArrayLength());
        Assert.Equal("Reembolso Empresa Ficticia", results.GetProperty("credits")[0].GetProperty("description").GetString());
    }

    [Fact]
    public async Task ConfirmingACredit_Returns422_AndNothingIsStored()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();
        await OcrConfirmIntegrationTests.AuthenticateWithCoupleAsync(client);
        var uploadId = await OcrConfirmIntegrationTests.UploadAndMarkReadyAsync(factory, client,
            [Debit(0, "Mercado", 80m), Credit(1, "Reembolso", 500m)]);

        var response = await client.PostAsJsonAsync($"/api/v1/ocr/{uploadId}/confirm", new { selectedIndices = new[] { 0, 1 } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/transactions");
        Assert.Equal(0, list.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task PartialConfirmation_LeavesTheRestForLater_AndNeverDuplicates()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();
        await OcrConfirmIntegrationTests.AuthenticateWithCoupleAsync(client);
        var uploadId = await OcrConfirmIntegrationTests.UploadAndMarkReadyAsync(factory, client,
            [Debit(0, "Mercado", 80m), Debit(1, "Farmácia", 35m), Debit(2, "Padaria", 12m)]);

        var first = await client.PostAsJsonAsync($"/api/v1/ocr/{uploadId}/confirm",
            new { selectedIndices = new[] { 0 }, keepJobOpen = true });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, firstBody.GetProperty("transactionsCreated").GetInt32());
        Assert.Equal(2, firstBody.GetProperty("remainingLines").GetInt32());

        var status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/ocr/{uploadId}/status");
        Assert.Equal("Ready", status.GetProperty("status").GetString());

        var results = await client.GetFromJsonAsync<JsonElement>($"/api/v1/ocr/{uploadId}/results");
        var states = results.GetProperty("candidates").EnumerateArray()
            .Select(c => c.GetProperty("lineState").GetString()).ToList();
        Assert.Equal(["Confirmed", "Pending", "Pending"], states);

        var second = await client.PostAsJsonAsync($"/api/v1/ocr/{uploadId}/confirm",
            new { selectedIndices = new[] { 0, 1, 2 }, keepJobOpen = true });
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, secondBody.GetProperty("transactionsCreated").GetInt32());
        Assert.Equal(1, secondBody.GetProperty("duplicatesSkipped").GetInt32());
        Assert.Equal(0, secondBody.GetProperty("remainingLines").GetInt32());

        status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/ocr/{uploadId}/status");
        Assert.Equal("Confirmed", status.GetProperty("status").GetString());
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/transactions");
        Assert.Equal(3, list.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task TheInstalledAppCall_StillClosesTheImportInOneRequest()
    {
        // Exactly what the app sends today: indexes, categories and edits, nothing else.
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();
        await OcrConfirmIntegrationTests.AuthenticateWithCoupleAsync(client);
        var uploadId = await OcrConfirmIntegrationTests.UploadAndMarkReadyAsync(factory, client,
            [Debit(0, "Mercado", 80m), Debit(1, "Farmácia", 35m)]);

        var response = await client.PostAsJsonAsync($"/api/v1/ocr/{uploadId}/confirm", new
        {
            selectedIndices = new[] { 0 },
            categoryOverrides = new[] { new { index = 0, category = "ALIMENTACAO" } },
            candidateEdits = new[] { new { index = 0, description = "Mercado do bairro" } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("transactionsCreated").GetInt32());
        var status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/ocr/{uploadId}/status");
        Assert.Equal("Confirmed", status.GetProperty("status").GetString());
    }

    [Fact]
    public async Task StatusOfAJobStuckInProcessing_ReportsTheTimeoutFailure()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();
        await OcrConfirmIntegrationTests.AuthenticateWithCoupleAsync(client);
        var uploadId = await OcrConfirmIntegrationTests.UploadAndMarkReadyAsync(factory, client, [Debit(0, "Mercado", 80m)]);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.ImportJobs.FindAsync(uploadId);
            job!.MarkProcessing(DateTime.UtcNow.AddMinutes(-30));
            await db.SaveChangesAsync();
        }

        var status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/ocr/{uploadId}/status");

        Assert.Equal("Failed", status.GetProperty("status").GetString());
        Assert.Equal("PROCESSING_TIMEOUT", status.GetProperty("errorCode").GetString());
    }

    private static OcrCandidate Debit(int index, string description, decimal amount)
    {
        var candidate = OcrConfirmIntegrationTests.Candidate(index, description, amount, $"fp-lifecycle-{index}");
        candidate.Type = TransactionType.Debit;
        return candidate;
    }

    private static OcrCandidate Credit(int index, string description, decimal amount)
    {
        var candidate = OcrConfirmIntegrationTests.Candidate(index, description, amount, string.Empty);
        candidate.Type = TransactionType.Credit;
        return candidate;
    }
}
