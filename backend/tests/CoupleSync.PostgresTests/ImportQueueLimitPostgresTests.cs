using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.PostgresTests;

/// <summary>Issue 56: the limit of unfinished imports per group, through the real API on real PostgreSQL.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ImportQueueLimitPostgresTests
{
    private readonly PostgresServer _server;

    public ImportQueueLimitPostgresTests(PostgresServer server) => _server = server;

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46]);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", "extrato.jpg");
        return await client.PostAsync("/api/v1/ocr/upload", content);
    }

    [PostgresFact]
    public async Task EightSimultaneousUploadsOfOneGroup_LeaveExactlyThreeUnfinishedJobs_AndTheRestGetTheErrorInPortuguese()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", joinCode: ana.JoinCode); // same group: shares the limit
        var carla = await factory.RegisterAsync("Carla");                           // other group

        var gate = new TaskCompletionSource();
        var calls = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            return await UploadAsync(i % 2 == 0 ? ana.Client : bruno.Client);
        })).ToList();
        gate.SetResult();
        var responses = await Task.WhenAll(calls);

        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        var refused = responses.Where(r => r.StatusCode == HttpStatusCode.Conflict).ToList();
        Assert.Equal(5, refused.Count);
        var body = await refused[0].Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IMPORT_QUEUE_FULL", body.GetProperty("code").GetString());
        Assert.Equal("Já há 3 extratos sendo lidos. Espere um deles terminar para enviar outro.", body.GetProperty("message").GetString());

        await using var db = factory.NewContext();
        Assert.Equal(3, await db.ImportJobs.IgnoreQueryFilters().CountAsync(j => j.CoupleId == ana.CoupleId));

        // another group is not affected
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(carla.Client)).StatusCode);
    }

    [PostgresFact]
    public async Task AFinishedJobFreesASlot_AndOnlyPendingAndProcessingCount()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var ana = await factory.RegisterAsync("Ana");
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await UploadAsync(ana.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(ana.Client)).StatusCode);

        await using (var db = factory.NewContext())
        {
            var jobs = await db.ImportJobs.IgnoreQueryFilters().Where(j => j.CoupleId == ana.CoupleId).OrderBy(j => j.CreatedAtUtc).ToListAsync();
            var now = DateTime.UtcNow;
            jobs[0].MarkFailed("PDF_TIMEOUT", "x", now);
            jobs[1].MarkProcessing(now);
            jobs[1].MarkReady("[]", now);
            await db.SaveChangesAsync();
        }

        // 1 left (Pending): room for two more
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(ana.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(ana.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(ana.Client)).StatusCode);
    }
}
