using System.Text.Json;
using CoupleSync.Api.Middleware;
using CoupleSync.Application.Reports;
using CoupleSync.Application.Dashboard;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoupleSync.UnitTests.Validation;

/// <summary>
/// A07 safety net — argument exceptions raised by our own domain/application rules are client
/// errors (400 in the standard envelope); anything else must remain a 500.
/// </summary>
public sealed class GlobalExceptionMiddlewareTests
{
    private static async Task<(int StatusCode, JsonElement Body)> InvokeAsync(RequestDelegate next)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new GlobalExceptionMiddleware(next, NullLogger<GlobalExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, document.RootElement.Clone());
    }

    [Fact]
    public async Task DomainArgumentException_Returns400InStandardEnvelope()
    {
        var (status, body) = await InvokeAsync(_ =>
        {
            var source = IncomeSource.Create(
                Guid.NewGuid(), Guid.NewGuid(), "2026-10", "Salário", 100m, "BRL", false, DateTime.UtcNow);
            source.Update("   ", null, null, null, DateTime.UtcNow);
            return Task.CompletedTask;
        });

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
        Assert.Equal("O nome é obrigatório e deve ter no máximo 64 caracteres.", body.GetProperty("message").GetString());
        Assert.True(body.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task DomainArgumentOutOfRangeException_Returns400()
    {
        var (status, body) = await InvokeAsync(_ =>
        {
            TransactionEventIngest.Create(
                Guid.NewGuid(), Guid.NewGuid(), "NUBANK", 0m, "BRL", DateTime.UtcNow, null, null, null, DateTime.UtcNow);
            return Task.CompletedTask;
        });

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
        Assert.Equal("O valor deve ser maior que zero.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ApplicationArgumentException_Returns400()
    {
        // The Application layer rejects an out-of-range month count with an ArgumentOutOfRangeException (the repository is never reached).
        var service = new ReportsService(
            null!,
            null!,
            new FixedDateTimeProvider(new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc)));

        var (status, body) = await InvokeAsync(async _ =>
        {
            await service.GetSpendingByCategoryAsync(Guid.NewGuid(), 0, CancellationToken.None);
        });

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task FrameworkArgumentException_StaysInternalServerError()
    {
        var (status, body) = await InvokeAsync(_ =>
        {
            // Genuine programming error raised by the BCL (duplicate key), not by a business rule.
            var duplicated = new[] { 1, 1 }.ToDictionary(x => x);
            Assert.Empty(duplicated);
            return Task.CompletedTask;
        });

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Equal("INTERNAL_SERVER_ERROR", body.GetProperty("code").GetString());
        Assert.Equal("Ocorreu um erro inesperado. Tente novamente em instantes.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ArgumentExceptionFromOutsideDomainAndApplication_StaysInternalServerError()
    {
        var (status, body) = await InvokeAsync(_ => throw new ArgumentException("leaky internal detail"));

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Equal("Ocorreu um erro inesperado. Tente novamente em instantes.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OtherExceptions_StayInternalServerError()
    {
        var (status, body) = await InvokeAsync(_ => throw new InvalidOperationException("boom"));

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Equal("INTERNAL_SERVER_ERROR", body.GetProperty("code").GetString());
    }
}
