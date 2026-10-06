using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using CoupleSync.Application.OcrImport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoupleSync.PostgresTests;

/// <summary>The main flows of the app through the real API on real PostgreSQL (migrations applied at host start).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ApiFlowTests
{
    private readonly PostgresServer _server;

    public ApiFlowTests(PostgresServer server) => _server = server;

    [PostgresFact]
    public async Task SignUp_Group_Transaction_Budget_Goal_Import_Reports_Incomes()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);

        // sign-up and group
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", joinCode: ana.JoinCode);
        Assert.Equal(ana.CoupleId, bruno.CoupleId);
        var me = await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal("Ana", me.GetProperty("name").GetString());

        // transactions: last month's (reports only count complete months) and this month's
        var lastMonth = DateTime.UtcNow.AddMonths(-1);
        var lastMonthDay = new DateTime(lastMonth.Year, lastMonth.Month, 15, 15, 0, 0, DateTimeKind.Utc);
        var created = await ana.Client.PostAsJsonAsync("/api/v1/transactions", new
        {
            amount = 120.50m, currency = "BRL", eventTimestampUtc = lastMonthDay, description = "Mercado", category = "ALIMENTACAO"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var transactionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await bruno.Client.PostAsJsonAsync("/api/v1/transactions", new
        {
            amount = 80m, currency = "BRL", eventTimestampUtc = lastMonthDay, description = "Cinema", category = "LAZER"
        })).StatusCode);

        // budget
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        var plan = await ana.Client.PostAsJsonAsync("/api/v1/budgets", new { month, grossIncome = 6000m, currency = "BRL" });
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        var planId = (await plan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var allocations = await ana.Client.PutAsJsonAsync($"/api/v1/budgets/{planId}/allocations", new
        {
            allocations = new[]
            {
                new { category = "ALIMENTACAO", allocatedAmount = 1500m, currency = "BRL" },
                new { category = "LAZER", allocatedAmount = 500m, currency = "BRL" },
            }
        });
        Assert.Equal(HttpStatusCode.OK, allocations.StatusCode);
        var current = await bruno.Client.GetFromJsonAsync<JsonElement>("/api/v1/budgets/current");
        Assert.Equal(2, current.GetProperty("allocations").GetArrayLength());

        // goal, linked to a transaction, then deleted
        var goal = await ana.Client.PostAsJsonAsync("/api/v1/goals", new
        {
            title = "Viagem", targetAmount = 5000m, currency = "BRL", deadline = DateTime.UtcNow.AddMonths(6)
        });
        Assert.Equal(HttpStatusCode.Created, goal.StatusCode);
        var goalId = (await goal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var link = await ana.Client.PatchAsJsonAsync($"/api/v1/transactions/{transactionId}/goal", new { goalId });
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);
        var progress = await bruno.Client.GetFromJsonAsync<JsonElement>($"/api/v1/goals/{goalId}/progress");
        Assert.Equal(120.50m, progress.GetProperty("contributedAmount").GetDecimal());

        // income
        var income = await ana.Client.PostAsJsonAsync("/api/v1/incomes", new { month, name = "Salário", amount = 4000m, currency = "BRL", isShared = false });
        Assert.Equal(HttpStatusCode.Created, income.StatusCode);
        var incomes = await bruno.Client.GetFromJsonAsync<JsonElement>("/api/v1/incomes/current");
        Assert.Equal(4000m, incomes.GetProperty("coupleTotal").GetDecimal());

        // confirmed import
        var uploadId = await UploadAndMarkReadyAsync(factory, ana.Client,
            Candidate(0, "Padaria", 18m, "pg-fp-0"), Candidate(1, "Farmácia", 42m, "pg-fp-1"));
        var confirm = await ana.Client.PostAsJsonAsync($"/api/v1/ocr/{uploadId}/confirm", new { selectedIndices = new[] { 0, 1 } });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal(2, (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transactionsCreated").GetInt32());
        var importStatus = await ana.Client.GetFromJsonAsync<JsonElement>($"/api/v1/ocr/{uploadId}/status");
        Assert.Equal("Confirmed", importStatus.GetProperty("status").GetString());

        // reports (PostgreSQL-specific grouping SQL)
        var spending = await bruno.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports/spending-by-category?months=3");
        var byCategory = spending.GetProperty("categories").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("total").GetDecimal());
        Assert.Equal(120.50m, byCategory.Single(c => c.Key.Contains("Aliment", StringComparison.OrdinalIgnoreCase)).Value);
        var trends = await bruno.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports/monthly-trends?months=3");
        Assert.Equal(3, trends.GetProperty("months").GetArrayLength());

        // listing, deletion
        var list = await bruno.Client.GetFromJsonAsync<JsonElement>("/api/v1/transactions");
        Assert.Equal(4, list.GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/transactions/{transactionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/goals/{goalId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ana.Client.GetAsync($"/api/v1/goals/{goalId}")).StatusCode);
        var after = await bruno.Client.GetFromJsonAsync<JsonElement>("/api/v1/transactions");
        Assert.Equal(3, after.GetProperty("totalCount").GetInt32());
    }

    [PostgresFact]
    public async Task ForeignKeys_NeverLetAUserOrAGroupDisappearUnderTheirData()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var ana = await factory.RegisterAsync("Ana");
        Assert.Equal(HttpStatusCode.Created, (await ana.Client.PostAsJsonAsync("/api/v1/transactions", new
        {
            amount = 10m, currency = "BRL", description = "x", category = "LAZER"
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ana.Client.PostAsJsonAsync("/api/v1/goals", new
        {
            title = "Meta", targetAmount = 100m, currency = "BRL", deadline = DateTime.UtcNow.AddMonths(2)
        })).StatusCode);

        // no cascade: deleting the owner of the data is refused, not propagated
        var userDelete = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            "DELETE FROM users WHERE id = @id", ("id", ana.UserId)));
        Assert.Equal("23503", userDelete.SqlState);
        var coupleDelete = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            "DELETE FROM couples WHERE id = @id", ("id", ana.CoupleId!.Value)));
        Assert.Equal("23503", coupleDelete.SqlState);
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM goals"));

        // and a row for a user/group that does not exist cannot be written
        var orphan = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            """
            INSERT INTO device_tokens (id, couple_id, created_at_utc, last_seen_at_utc, platform, token, user_id)
            VALUES (gen_random_uuid(), @couple, now(), now(), 'android', 'x', gen_random_uuid())
            """, ("couple", ana.CoupleId.Value)));
        Assert.Equal("23503", orphan.SqlState);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    internal static OcrCandidate Candidate(int index, string description, decimal amount, string fingerprint) => new()
    {
        Index = index,
        Date = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc),
        Description = description,
        Amount = amount,
        Currency = "BRL",
        Confidence = 1.0,
        Fingerprint = fingerprint,
    };

    /// <summary>Uploads a file as the client's user, then moves the job to Ready with the given candidates.</summary>
    internal static async Task<Guid> UploadAndMarkReadyAsync(PostgresApiFactory factory, HttpClient client, params OcrCandidate[] candidates)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", "statement.jpg");
        var upload = await client.PostAsync("/api/v1/ocr/upload", content);
        upload.EnsureSuccessStatusCode();
        var uploadId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetGuid();

        await using var db = factory.NewContext();
        var job = await db.ImportJobs.SingleAsync(j => j.Id == uploadId);
        var now = DateTime.UtcNow;
        job.MarkProcessing(now);
        job.MarkReady(JsonSerializer.Serialize(candidates), now);
        await db.SaveChangesAsync();
        return uploadId;
    }
}
