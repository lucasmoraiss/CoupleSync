using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.OcrImport;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Infrastructure.BackgroundJobs;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Worker;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.TestSupport;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoupleSync.IntegrationTests.OcrImport;

/// <summary>
/// Issue #14: the process that reads the PDF is killed in the middle of the read. The import job ends as Failed with its
/// own code (not Processing), the API keeps answering, and the app sees the failure through the same status route.
/// </summary>
[Trait("Category", "Ocr")]
public sealed class PdfWorkerCrashIntegrationTests
{
    [Fact]
    public async Task AWorkerKilledMidRead_FailsTheJob_AndTheApiKeepsAnswering()
    {
        await using var baseFactory = new OcrWebApplicationFactory();
        await using var host = baseFactory.WithTestHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IStorageAdapter>();
            services.AddSingleton<IStorageAdapter, PdfStorage>();
            services.RemoveAll<IPdfTextExtractor>();
            services.AddSingleton<IPdfTextExtractor>(new ChildProcessPdfTextExtractor(KilledWorker()));
            services.RemoveAll<IOcrProvider>();
            services.AddScoped<IOcrProvider, LocalPdfParserProvider>();
            services.AddSingleton(new BankDetector([]));
        }));
        using var client = host.CreateClient();
        await OcrConfirmIntegrationTests.AuthenticateWithCoupleAsync(client);

        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n% synthetic\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", "extrato.pdf");
        var upload = await client.PostAsync("/api/v1/ocr/upload", content);
        upload.EnsureSuccessStatusCode();
        var uploadId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetGuid();

        var backgroundJob = new OcrBackgroundJob(host.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OcrBackgroundJob>.Instance);
        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        var status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/ocr/{uploadId}/status");
        Assert.Equal("Failed", status.GetProperty("status").GetString());
        Assert.Equal("PDF_WORKER_FAILED", status.GetProperty("errorCode").GetString());
        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    private static PdfWorkerOptions KilledWorker()
    {
        void KillSoon(int id) => _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            Process.GetProcessById(id).Kill(entireProcessTree: true);
        });

        return OperatingSystem.IsWindows()
            ? new PdfWorkerOptions { FileName = "cmd.exe", Arguments = ["/c", "ping -n 60 127.0.0.1 > nul"], OnProcessStarted = KillSoon }
            : new PdfWorkerOptions { FileName = "sh", Arguments = ["-c", "sleep 60"], OnProcessStarted = KillSoon };
    }

    private sealed class PdfStorage : IStorageAdapter
    {
        public Task<string> UploadAsync(Guid coupleId, Guid uploadId, Stream content, string mimeType, CancellationToken ct)
            => Task.FromResult($"fake/couples/{coupleId}/{uploadId}");

        public Task<Stream> DownloadAsync(string storagePath, CancellationToken ct)
            => Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4\n")));

        public Task DeleteAsync(string storagePath, CancellationToken ct) => Task.CompletedTask;
    }
}
