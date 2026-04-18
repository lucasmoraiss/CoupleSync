using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.Infrastructure.Persistence.Seeders;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.UnitTests.Persistence;

public sealed class CategoryRulesSeederTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public CategoryRulesSeederTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();
    }

    private AppDbContext CreateContext() => new(_options, coupleContext: null);

    [Fact]
    public async Task SeedAsync_FirstRun_InsertsRules()
    {
        using var ctx = CreateContext();
        var seeder = new CategoryRulesSeeder(ctx);

        await seeder.SeedAsync();

        var count = await ctx.CategoryRules.CountAsync();
        Assert.True(count >= 20, $"Expected at least 20 category rules, got {count}");
    }

    [Fact]
    public async Task SeedAsync_CalledTwice_IsIdempotent_NoDuplicates()
    {
        using (var ctx1 = CreateContext())
        {
            var seeder1 = new CategoryRulesSeeder(ctx1);
            await seeder1.SeedAsync();
        }

        int countAfterFirst;
        using (var ctx2 = CreateContext())
        {
            countAfterFirst = await ctx2.CategoryRules.CountAsync();
        }

        using (var ctx3 = CreateContext())
        {
            var seeder2 = new CategoryRulesSeeder(ctx3);
            await seeder2.SeedAsync();
        }

        using (var ctx4 = CreateContext())
        {
            var countAfterSecond = await ctx4.CategoryRules.CountAsync();
            Assert.Equal(countAfterFirst, countAfterSecond);
        }
    }

    [Fact]
    public async Task SeedAsync_ContainsUberEats_WithHigherPriorityThanUber()
    {
        using var ctx = CreateContext();
        var seeder = new CategoryRulesSeeder(ctx);

        await seeder.SeedAsync();

        var uberEats = await ctx.CategoryRules.FirstOrDefaultAsync(r => r.Keyword == "UBER EATS");
        var uber = await ctx.CategoryRules.FirstOrDefaultAsync(r => r.Keyword == "UBER");

        Assert.NotNull(uberEats);
        Assert.NotNull(uber);
        Assert.True(uberEats!.Priority > uber!.Priority,
            $"UBER EATS priority ({uberEats.Priority}) must be higher than UBER priority ({uber.Priority})");
        Assert.Equal("Alimentação", uberEats.Category);
        Assert.Equal("Transporte", uber.Category);
    }

    public void Dispose() => _connection.Dispose();
}
