using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleSync.IntegrationTests.Transactions;

[Trait("Category", "TransactionEdit")]
public sealed class TransactionEditIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Patch_ChangesAllFields_AndTheListShowsThem()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await LoginNewCoupleAsync(client);
        var id = await CreateManualAsync(client, 50m);
        var newDate = DateTime.UtcNow.AddDays(-2);

        var response = await client.PatchAsJsonAsync($"/api/v1/transactions/{id}", new
        {
            Amount = 123.45m,
            Description = "Mercado do mês",
            EventTimestampUtc = newDate,
            Category = "Saúde",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/transactions", Json);
        var item = list.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(123.45m, item.GetProperty("amount").GetDecimal());
        Assert.Equal("Mercado do mês", item.GetProperty("description").GetString());
        Assert.Equal("SAUDE", item.GetProperty("category").GetString());
        // SQLite hands the value back without a zone marker; it is UTC either way.
        var stored = DateTime.SpecifyKind(item.GetProperty("eventTimestampUtc").GetDateTime(), DateTimeKind.Utc);
        Assert.Equal(newDate, stored, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Patch_PartialUpdate_KeepsTheOtherFields()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await LoginNewCoupleAsync(client);
        var id = await CreateManualAsync(client, 50m);

        var response = await client.PatchAsJsonAsync($"/api/v1/transactions/{id}", new { Amount = 75m });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(75m, body.GetProperty("amount").GetDecimal());
        Assert.Equal("Almoço", body.GetProperty("description").GetString());
        Assert.Equal("ALIMENTACAO", body.GetProperty("category").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"amount\":0}")]
    [InlineData("{\"amount\":10.555}")]
    [InlineData("{\"category\":\"Inexistente\"}")]
    public async Task Patch_InvalidBody_Returns400InTheSingleErrorFormat(string body)
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await LoginNewCoupleAsync(client);
        var id = await CreateManualAsync(client, 50m);

        var response = await client.PatchAsync(
            $"/api/v1/transactions/{id}", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("VALIDATION_ERROR", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_TransactionOfAnotherCouple_Returns404()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await LoginNewCoupleAsync(client);
        var id = await CreateManualAsync(client, 50m);
        await LoginNewCoupleAsync(client);

        var response = await client.PatchAsJsonAsync($"/api/v1/transactions/{id}", new { Amount = 1m });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("TRANSACTION_NOT_FOUND", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_ImportedTransaction_KeepsItsFingerprint_SoReimportDoesNotRecreateIt()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await LoginNewCoupleAsync(client);
        var ingest = new
        {
            Bank = "NUBANK",
            Amount = 99.99m,
            Currency = "BRL",
            EventTimestamp = DateTime.UtcNow.AddMinutes(-5),
            Description = "Test purchase",
            Merchant = "Test Store",
            RawNotificationText = "Nubank: R$99,99 at Test Store",
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/integrations/events", ingest)).StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/transactions", Json);
        var id = list.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        string fingerprintBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            fingerprintBefore = (await db.Transactions.IgnoreQueryFilters().SingleAsync(t => t.Id == id)).Fingerprint;
        }

        var patch = await client.PatchAsJsonAsync($"/api/v1/transactions/{id}", new { Amount = 10m, Category = "Lazer" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        await client.PostAsJsonAsync("/api/v1/integrations/events", ingest);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var all = await db.Transactions.IgnoreQueryFilters().ToListAsync();
            var tx = Assert.Single(all);
            Assert.Equal(fingerprintBefore, tx.Fingerprint);
            Assert.Equal(10m, tx.Amount);
        }
    }

    [Fact]
    public async Task Patch_Merchant_IsStored_AndKeepsTheFingerprintOfAnImportedTransaction()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await LoginNewCoupleAsync(client);
        var ingest = new
        {
            Bank = "NUBANK",
            Amount = 20m,
            Currency = "BRL",
            EventTimestamp = DateTime.UtcNow.AddMinutes(-5),
            Description = "Compra",
            Merchant = "Loja Velha",
            RawNotificationText = "Nubank: R$20,00 at Loja Velha",
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/integrations/events", ingest)).StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/transactions", Json);
        var id = list.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();

        var response = await client.PatchAsJsonAsync($"/api/v1/transactions/{id}", new { Merchant = "Loja Nova" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("Loja Nova", body.GetProperty("merchant").GetString());
        await client.PostAsJsonAsync("/api/v1/integrations/events", ingest);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tx = Assert.Single(await db.Transactions.IgnoreQueryFilters().ToListAsync());
        Assert.Equal("Loja Nova", tx.Merchant);
    }

    [Fact]
    public async Task Patch_MerchantTooLong_Returns400()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await LoginNewCoupleAsync(client);
        var id = await CreateManualAsync(client, 50m);

        var response = await client.PatchAsJsonAsync($"/api/v1/transactions/{id}", new { Merchant = new string('x', 513) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_WithIdenticalTimestamps_PagesWithoutSkippingOrRepeatingRows()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await LoginNewCoupleAsync(client);
        var sameInstant = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < 7; i++)
        {
            var created = await client.PostAsJsonAsync("/api/v1/transactions", new
            {
                Amount = 10m + i, Currency = "BRL", Category = "Alimentação", EventTimestampUtc = sameInstant,
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var seen = new List<Guid>();
        for (var page = 1; page <= 4; page++)
        {
            var result = await client.GetFromJsonAsync<JsonElement>($"/api/v1/transactions?page={page}&pageSize=2", Json);
            seen.AddRange(result.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var expected = await db.Transactions.IgnoreQueryFilters()
            .OrderByDescending(t => t.EventTimestampUtc).ThenByDescending(t => t.Id)
            .Select(t => t.Id).ToListAsync();
        Assert.Equal(expected, seen);
    }

    private static async Task<Guid> CreateManualAsync(HttpClient client, decimal amount)
    {
        var response = await client.PostAsJsonAsync("/api/v1/transactions", new
        {
            Amount = amount,
            Currency = "BRL",
            Description = "Almoço",
            Category = "Alimentação",
            EventTimestampUtc = DateTime.UtcNow,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return body.GetProperty("id").GetGuid();
    }

    /// <summary>Registers a user, creates a couple and leaves the client authenticated as that user.</summary>
    private static async Task LoginNewCoupleAsync(HttpClient client)
    {
        const string password = "SecurePass123!";
        var email = $"edit-{Guid.NewGuid():N}@example.com";
        client.DefaultRequestHeaders.Authorization = null;
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new { Email = email, Name = "Test User", Password = password });
        register.EnsureSuccessStatusCode();
        var token = (await register.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/couples", new { })).StatusCode);

        client.DefaultRequestHeaders.Authorization = null;
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password = password });
        login.EnsureSuccessStatusCode();
        var loginToken = (await login.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginToken);
    }
}
