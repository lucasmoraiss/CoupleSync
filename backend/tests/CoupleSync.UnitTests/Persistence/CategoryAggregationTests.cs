using CoupleSync.Domain.Entities;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.UnitTests.Persistence;

/// <summary>
/// Dashboard, reports, cash flow and the budget "spent" figure add "Alimentação", "ALIMENTACAO" and
/// "alimentacao" together (rows written before the normalization migration) and leave transactions
/// in another currency out of the sums in reais.
/// </summary>
public sealed class CategoryAggregationTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _couple = Guid.NewGuid();
    private readonly Guid _otherCouple = Guid.NewGuid();
    private int _sequence;

    public CategoryAggregationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var ctx = new AppDbContext(_options, coupleContext: null);
        ctx.Database.EnsureCreated();

        // Rows are inserted without their ingest events: only the aggregation matters here.
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF";
            pragma.ExecuteNonQuery();
        }

        using var seed = new AppDbContext(_options, coupleContext: null);
        Add(seed, _couple, 100m, "Alimentação");
        Add(seed, _couple, 50m, "ALIMENTACAO");
        Add(seed, _couple, 25m, "alimentacao");
        Add(seed, _couple, 10m, " Alimentação ");
        Add(seed, _couple, 30m, "Transporte");
        Add(seed, _couple, 5m, "Mercado");          // legacy free text -> OUTROS
        Add(seed, _couple, 7m, "OUTROS");
        Add(seed, _couple, 999m, "Alimentação", currency: "USD");
        Add(seed, _couple, 888m, "Lazer", currency: "EUR");
        Add(seed, _otherCouple, 4000m, "Alimentação");
        seed.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext CreateContext() => new(_options, coupleContext: null);

    private void Add(AppDbContext ctx, Guid couple, decimal amount, string category, string currency = "BRL")
    {
        var n = ++_sequence;
        ctx.Transactions.Add(Transaction.Create(
            couple, Guid.NewGuid(), $"fp-{n}", "NUBANK", amount, currency,
            Start.AddDays(n), "d", "m", category, Guid.NewGuid(), Start));
    }

    [Fact]
    public async Task Dashboard_SumsCategoryVariantsTogether_AndSkipsOtherCurrencies()
    {
        using var ctx = CreateContext();

        var result = await new DashboardRepository(ctx).GetAggregatesAsync(_couple, Start, End, CancellationToken.None);

        Assert.Equal(185m + 30m + 12m, result.TotalExpenses);
        Assert.Equal(7, result.TransactionCount);
        Assert.Equal(185m, result.ExpensesByCategory["ALIMENTACAO"]);
        Assert.Equal(30m, result.ExpensesByCategory["TRANSPORTE"]);
        Assert.Equal(12m, result.ExpensesByCategory["OUTROS"]);
        Assert.Equal(3, result.ExpensesByCategory.Count);
    }

    [Fact]
    public async Task Reports_SumCategoryVariantsTogether_AndSkipOtherCurrencies()
    {
        using var ctx = CreateContext();

        var rows = await new ReportsRepository(ctx).GetSpendingByCategoryAsync(_couple, Start, End, CancellationToken.None);

        Assert.Equal(185m, rows.Single(r => r.Category == "ALIMENTACAO").Total);
        Assert.Equal(12m, rows.Single(r => r.Category == "OUTROS").Total);
        Assert.DoesNotContain(rows, r => r.Category == "Lazer" || r.Category == "LAZER");
        Assert.Equal(3, rows.Count);

        var monthly = await new ReportsRepository(ctx).GetMonthlySpendingAsync(_couple, Start, End, CancellationToken.None);
        Assert.Equal(227m, monthly.Sum(m => m.Total));
    }

    [Fact]
    public async Task CashFlow_SumsCategoryVariantsTogether_AndSkipsOtherCurrencies()
    {
        using var ctx = CreateContext();

        var data = await new CashFlowRepository(ctx).GetHistoricalDataAsync(_couple, Start, End, CancellationToken.None);

        Assert.Equal(227m, data.TotalSpend);
        Assert.Equal(185m, data.CategoryBreakdown["ALIMENTACAO"]);
        Assert.Equal(3, data.CategoryBreakdown.Count);
    }

    [Fact]
    public async Task BudgetSpentByCategory_SumsCategoryVariantsTogether_AndSkipsOtherCurrencies()
    {
        using var ctx = CreateContext();

        var spent = await new TransactionRepository(ctx).GetActualSpentByCategoryAsync(_couple, Start, End, CancellationToken.None);

        Assert.Equal(185m, spent["ALIMENTACAO"]);
        Assert.Equal(30m, spent["TRANSPORTE"]);
        Assert.Equal(12m, spent["OUTROS"]);
        Assert.Equal(3, spent.Count);
    }
}
