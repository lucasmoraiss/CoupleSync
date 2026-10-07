using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CoupleSync.Api.Validators;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CoupleSync.InvariantGlobalizationTests;

/// <summary>
/// The production image once ran without ICU (globalization-invariant mode): the API did not start
/// (<c>new CultureInfo("pt-BR")</c> throws) and accented categories were rejected (<c>string.Normalize</c> does
/// nothing). This assembly runs in that mode on purpose, so the API has to keep working without culture data.
/// </summary>
public sealed class InvariantGlobalizationTests
{
    [Fact]
    public void ThisTestProcess_ReallyRunsInInvariantMode()
    {
        // Guards the guard: if the project setting stops reaching the test host, the other tests prove nothing.
        Assert.Throws<CultureNotFoundException>(() => new CultureInfo("pt-BR"));
        Assert.Equal("ã", "ã".Normalize(NormalizationForm.FormD));
    }

    [Theory]
    [InlineData("Alimentação", "ALIMENTACAO")]
    [InlineData("alimentação", "ALIMENTACAO")]
    [InlineData("ALIMENTAÇÃO", "ALIMENTACAO")]
    [InlineData("Saúde", "SAUDE")]
    [InlineData("SAÚDE", "SAUDE")]
    [InlineData("Alimentação", "ALIMENTACAO")]   // decomposed (combining cedilla and tilde)
    [InlineData("Saúde", "SAUDE")]
    public void AccentedCategories_AreRecognisedWithoutIcu(string input, string expected)
        => Assert.Equal(expected, TransactionCategories.TryNormalize(input));

    [Fact]
    public void ValidationMessages_AreConfiguredInPortugueseWithoutIcu()
    {
        ValidationLocalization.Configure();

        var result = new ProbeValidator().Validate(new Probe(string.Empty));

        Assert.Equal("'E-mail' deve ser informado.", Assert.Single(result.Errors).ErrorMessage);
    }

    [Fact]
    public async Task TheApiStarts_AcceptsAccentedCategories_AndAnswersValidationErrorsInPortuguese()
    {
        await using var factory = new InvariantApiFactory();
        using var client = factory.CreateClient();

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { Email = $"inv-{Guid.NewGuid():N}@example.com", Name = "Teste", Password = "SecurePass123!" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        Authorize(client, await register.Content.ReadFromJsonAsync<JsonElement>());

        var couple = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, couple.StatusCode);
        Authorize(client, await couple.Content.ReadFromJsonAsync<JsonElement>());

        foreach (var (category, key) in new[] { ("Alimentação", "ALIMENTACAO"), ("Saúde", "SAUDE") })
        {
            var created = await PostJsonAsync(client, "/api/v1/transactions",
                "{\"amount\":25.9,\"currency\":\"BRL\",\"eventTimestampUtc\":\"2026-10-01T12:00:00Z\",\"description\":\"Teste\",\"category\":\"" + category + "\"}");
            Assert.True(HttpStatusCode.Created == created.StatusCode, $"{category}: {(int)created.StatusCode} {await created.Content.ReadAsStringAsync()}");
            Assert.Equal(key, (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("category").GetString());
        }

        var invalid = await PostJsonAsync(client, "/api/v1/transactions",
            "{\"amount\":0,\"currency\":\"BRL\",\"eventTimestampUtc\":\"2026-10-01T12:00:00Z\",\"description\":\"\",\"category\":\"Lazer\"}");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var body = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("VALIDATION_ERROR", body);
        Assert.DoesNotContain("must", body);
        Assert.Contains("valor", body, StringComparison.OrdinalIgnoreCase);
    }

    private static void Authorize(HttpClient client, JsonElement body)
    {
        if (body.TryGetProperty("accessToken", out var token) && token.GetString() is { Length: > 0 } value)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", value);
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, string json)
        => client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

    private sealed record Probe(string Email);

    private sealed class ProbeValidator : AbstractValidator<Probe>
    {
        public ProbeValidator() => RuleFor(x => x.Email).NotEmpty();
    }
}

internal sealed class InvariantApiFactory : WebApplicationFactory<Program>
{
    private const string JwtSecret = "invariant-test-secret-1234567890-abcdef";

    private readonly string _connectionString = $"Data Source=couplesync-invariant-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection? _keepAlive;

    public InvariantApiFactory() => Environment.SetEnvironmentVariable("JWT__SECRET", JwtSecret);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = JwtSecret,
        }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();

            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connectionString));

            using var scope = services.BuildServiceProvider().CreateScope();
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        _keepAlive?.Dispose();
        Environment.SetEnvironmentVariable("JWT__SECRET", null);
    }
}
