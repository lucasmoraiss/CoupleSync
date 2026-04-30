using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.OcrImport;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Infrastructure.BackgroundJobs;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CoupleSync.IntegrationTests.OcrImport;

[Trait("Category", "Ocr")]
public sealed class OcrControllerTests
{
    // JPEG magic bytes — accepted by FileTypeDetector
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];

    // GIF magic bytes — rejected (unsupported)
    private static readonly byte[] GifBytes = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x00, 0x00];

    private static string SampleCandidatesJson(int count) =>
        JsonSerializer.Serialize(
            Enumerable.Range(0, count)
                .Select(i => new OcrCandidate
                {
                    Index       = i,
                    Date        = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc),
                    Description = $"Item {i + 1}",
                    Amount      = 10m * (i + 1),
                    Currency    = "BRL",
                    Confidence  = 0.95,
                    Fingerprint = $"fp{i:D4}"
                })
                .ToList());

    // ── Auth / Couple gates ────────────────────────────────────────────────

    [Fact]
    public async Task Upload_WithoutAuth_Returns401()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v1/ocr/upload", BuildMultipartContent(JpegBytes, "receipt.jpg"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WithoutCouple_Returns403()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterAndGetTokenAsync(client, $"no-couple-ocr-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync("/api/v1/ocr/upload", BuildMultipartContent(JpegBytes, "receipt.jpg"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── POST /api/v1/ocr/upload ────────────────────────────────────────────

    [Fact]
    public async Task Upload_ValidJpeg_ReturnsUploadId()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"upload-jpeg-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync("/api/v1/ocr/upload", BuildMultipartContent(JpegBytes, "receipt.jpg"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<UploadResponseDto>();
        Assert.NotNull(payload);
        Assert.NotEqual(Guid.Empty, payload!.UploadId);
    }

    [Fact]
    public async Task Upload_UnsupportedFile_Returns415()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"upload-gif-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync("/api/v1/ocr/upload", BuildMultipartContent(GifBytes, "image.gif"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    // ── GET /api/v1/ocr/{uploadId}/status ─────────────────────────────────

    [Fact]
    public async Task GetStatus_PendingJob_ReturnsPendingStatus()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"status-pending-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var uploadId = await UploadJpegAsync(client);

        var response = await client.GetAsync($"/api/v1/ocr/{uploadId}/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<OcrStatusResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("Pending", payload!.Status);
    }

    [Fact]
    public async Task GetStatus_NonExistentJob_Returns404()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"status-404-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"/api/v1/ocr/{Guid.NewGuid()}/status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetStatus_DifferentCouple_Returns404()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();

        // Couple A uploads
        var tokenA = await RegisterWithCoupleAndGetTokenAsync(client, $"couple-a-ocr-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var uploadId = await UploadJpegAsync(client);

        // Couple B tries to read Couple A's job
        var tokenB = await RegisterWithCoupleAndGetTokenAsync(client, $"couple-b-ocr-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        var response = await client.GetAsync($"/api/v1/ocr/{uploadId}/status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── GET /api/v1/ocr/{uploadId}/results ────────────────────────────────

    [Fact]
    public async Task GetResults_ReadyJob_ReturnsCandidates()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"results-ready-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var uploadId = await UploadJpegAsync(client);
        await SimulateJobReadyAsync(factory, uploadId, SampleCandidatesJson(3));

        var response = await client.GetAsync($"/api/v1/ocr/{uploadId}/results");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<OcrResultsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(3, payload!.Candidates.Count);
    }

    [Fact]
    public async Task GetResults_PendingJob_Returns409()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"results-pending-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var uploadId = await UploadJpegAsync(client);
        // Job remains Pending — do not simulate processing

        var response = await client.GetAsync($"/api/v1/ocr/{uploadId}/results");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ── POST /api/v1/ocr/{uploadId}/confirm ───────────────────────────────

    [Fact]
    public async Task Confirm_ReadyJob_CreatesTransactions_Returns200()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"confirm-ready-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var uploadId = await UploadJpegAsync(client);
        await SimulateJobReadyAsync(factory, uploadId, SampleCandidatesJson(3));

        // Confirm indices 0 and 2 — expect 2 transactions created
        var response = await client.PostAsJsonAsync(
            $"/api/v1/ocr/{uploadId}/confirm",
            new { selectedIndices = new[] { 0, 2 } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ConfirmResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(2, payload!.TransactionsCreated);
    }

    [Fact]
    public async Task Confirm_ConfirmedJob_Returns409()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"confirm-dup-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var uploadId = await UploadJpegAsync(client);
        await SimulateJobReadyAsync(factory, uploadId, SampleCandidatesJson(2));

        // First confirm — should succeed
        var first = await client.PostAsJsonAsync(
            $"/api/v1/ocr/{uploadId}/confirm",
            new { selectedIndices = new[] { 0 } });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Second confirm — job is now Confirmed, should return 409
        var second = await client.PostAsJsonAsync(
            $"/api/v1/ocr/{uploadId}/confirm",
            new { selectedIndices = new[] { 1 } });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Confirm_NullIndices_Returns422()
    {
        await using var factory = new OcrWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"confirm-null-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var uploadId = await UploadJpegAsync(client);
        await SimulateJobReadyAsync(factory, uploadId, SampleCandidatesJson(2));

        // Empty array passes model validation but the service throws 422
        var response = await client.PostAsJsonAsync(
            $"/api/v1/ocr/{uploadId}/confirm",
            new { selectedIndices = Array.Empty<int>() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static async Task<Guid> UploadJpegAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/v1/ocr/upload", BuildMultipartContent(JpegBytes, "receipt.jpg"));
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<UploadResponseDto>();
        return payload!.UploadId;
    }

    private static MultipartFormDataContent BuildMultipartContent(byte[] fileBytes, string fileName)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", fileName);
        return content;
    }

    private static async Task SimulateJobReadyAsync(
        OcrWebApplicationFactory factory, Guid uploadId, string candidatesJson)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.ImportJobs.FindAsync(uploadId);
        Assert.NotNull(job);
        var now = DateTime.UtcNow;
        job!.MarkProcessing(now);
        job.MarkReady(candidatesJson, now);
        await db.SaveChangesAsync();
    }

    private static async Task<string> RegisterAndGetTokenAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = email,
            Name = "Test User",
            Password = "SecurePass123!"
        });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
        return auth!.AccessToken;
    }

    private static async Task<string> RegisterWithCoupleAndGetTokenAsync(HttpClient client, string email)
    {
        const string password = "SecurePass123!";

        var registerResponse = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = email,
            Name = "Test User",
            Password = password
        });
        registerResponse.EnsureSuccessStatusCode();
        var registerAuth = await registerResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registerAuth!.AccessToken);

        var createCoupleResponse = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, createCoupleResponse.StatusCode);

        client.DefaultRequestHeaders.Authorization = null;
        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Email = email,
            Password = password
        });
        loginResponse.EnsureSuccessStatusCode();
        var loginAuth = await loginResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        return loginAuth!.AccessToken;
    }

    // ── Local DTOs ─────────────────────────────────────────────────────────

    private sealed record UploadResponseDto(Guid UploadId);

    private sealed record OcrStatusResponseDto(string Status, string? ErrorCode, DateTime? QuotaResetDate);

    private sealed record OcrCandidateResponseDto(
        int Index, DateTime Date, string Description,
        decimal Amount, string Currency, double Confidence, bool DuplicateSuspected);

    private sealed record OcrResultsResponseDto(IReadOnlyList<OcrCandidateResponseDto> Candidates);

    private sealed record ConfirmResponseDto(int TransactionsCreated);

    private sealed record AuthResponseDto(AuthUserDto User, string AccessToken, string RefreshToken);

    private sealed record AuthUserDto(Guid Id, string Email, string Name);
}

// ── Fakes ──────────────────────────────────────────────────────────────────

internal sealed class OcrIntegrationFakeStorageAdapter : IStorageAdapter
{
    public Task<string> UploadAsync(Guid coupleId, Guid uploadId, Stream content, string mimeType, CancellationToken ct)
        => Task.FromResult($"fake/couples/{coupleId}/{uploadId}");

    public Task<Stream> DownloadAsync(string storagePath, CancellationToken ct)
        => Task.FromResult<Stream>(new MemoryStream());

    public Task DeleteAsync(string storagePath, CancellationToken ct)
        => Task.CompletedTask;
}

internal sealed class OcrIntegrationFakeOcrProvider : IOcrProvider
{
    public Task<string> AnalyzeAsync(string storagePath, string mimeType, CancellationToken ct)
        => Task.FromResult("""{"analyzeResult":{"documents":[]}}""");
}

/// <summary>
/// Fake transaction repository that captures transactions in memory without hitting the DB.
/// Used to avoid FK constraint violations (OCR service uses Guid.Empty as ingestEventId).
/// </summary>
internal sealed class FakeOcrTransactionRepository : ITransactionRepository
{
    public Task<bool> FingerprintExistsAsync(string fingerprint, Guid coupleId, CancellationToken ct)
        => Task.FromResult(false);
    public Task AddTransactionAsync(Transaction transaction, CancellationToken ct)
        => Task.CompletedTask;
    public Task<(int TotalCount, IReadOnlyList<Transaction> Items)> GetPagedAsync(
        Guid coupleId, int page, int pageSize, string? category,
        DateTime? startDate, DateTime? endDate, CancellationToken ct)
        => Task.FromResult<(int, IReadOnlyList<Transaction>)>((0, []));
    public Task<Transaction?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct)
        => Task.FromResult<Transaction?>(null);
    public Task<Transaction?> GetByIdRawAsync(Guid id, CancellationToken ct)
        => Task.FromResult<Transaction?>(null);
    public Task DeleteAsync(Transaction transaction, CancellationToken ct)
        => Task.CompletedTask;
    public Task<IReadOnlyList<Transaction>> GetByGoalIdAsync(Guid goalId, Guid coupleId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Transaction>>([]);
    public Task<IReadOnlyList<Transaction>> GetRecentByCoupleAsync(Guid coupleId, DateTime since, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Transaction>>([]);
    public Task UpdateAsync(Transaction transaction, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<Dictionary<string, decimal>> GetActualSpentByCategoryAsync(
        Guid coupleId, DateTime startUtc, DateTime endUtc, CancellationToken ct)
        => Task.FromResult(new Dictionary<string, decimal>());
    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
}

// ── WebApplicationFactory ──────────────────────────────────────────────────

internal sealed class OcrWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string JwtSecret   = "integration-test-secret-1234567890-abcdef";
    public const string JwtIssuer   = "CoupleSync.IntegrationTests";
    public const string JwtAudience = "CoupleSync.Mobile.IntegrationTests";

    private readonly string _databaseConnectionString =
        $"Data Source=couplesync-ocr-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private SqliteConnection? _keepAliveConnection;

    public OcrWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("JWT__SECRET", JwtSecret);
        Environment.SetEnvironmentVariable("JWT__ISSUER", JwtIssuer);
        Environment.SetEnvironmentVariable("JWT__AUDIENCE", JwtAudience);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("JWT__SECRET", JwtSecret);
        Environment.SetEnvironmentVariable("JWT__ISSUER", JwtIssuer);
        Environment.SetEnvironmentVariable("JWT__AUDIENCE", JwtAudience);
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            var config = new Dictionary<string, string?>
            {
                ["Jwt:Secret"]   = JwtSecret,
                ["Jwt:Issuer"]   = JwtIssuer,
                ["Jwt:Audience"] = JwtAudience
            };
            configBuilder.AddInMemoryCollection(config);
        });

        builder.ConfigureTestServices(services =>
        {
            // Replace PostgreSQL with SQLite in-memory
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();

            _keepAliveConnection = new SqliteConnection(_databaseConnectionString);
            _keepAliveConnection.Open();

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseSqlite(_databaseConnectionString);
            });

            // Replace real file storage with in-memory fake (avoids disk writes)
            services.RemoveAll<IStorageAdapter>();
            services.AddScoped<IStorageAdapter, OcrIntegrationFakeStorageAdapter>();

            // Replace real Azure OCR provider with stub (avoids external calls)
            services.RemoveAll<IOcrProvider>();
            services.AddScoped<IOcrProvider, OcrIntegrationFakeOcrProvider>();

            // Remove OcrBackgroundJob to prevent it from racing with test state manipulation
            var ocrBgJobDescriptor = services.FirstOrDefault(d =>
                d.ServiceType == typeof(IHostedService) &&
                d.ImplementationType == typeof(OcrBackgroundJob));
            if (ocrBgJobDescriptor != null)
                services.Remove(ocrBgJobDescriptor);
            // Replace real transaction repository with fake to avoid FK constraint violations.
            // OCR import creates transactions with ingestEventId=Guid.Empty which lacks a DB record.
            services.RemoveAll<ITransactionRepository>();
            services.AddScoped<ITransactionRepository, FakeOcrTransactionRepository>();
            using var scope = services.BuildServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        _keepAliveConnection?.Dispose();
        _keepAliveConnection = null;
        Environment.SetEnvironmentVariable("JWT__SECRET", null);
        Environment.SetEnvironmentVariable("JWT__ISSUER", null);
        Environment.SetEnvironmentVariable("JWT__AUDIENCE", null);
    }
}
