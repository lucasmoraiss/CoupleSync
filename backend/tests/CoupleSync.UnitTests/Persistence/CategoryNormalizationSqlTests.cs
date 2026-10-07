using System.Globalization;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.UnitTests.Persistence;

/// <summary>
/// Runs, on SQLite, the very statements the NormalizeCategoriesAndCurrency migration sends to
/// PostgreSQL (CategoryNormalizationSql.UpStatements) against legacy-looking data, and checks the SQL
/// mapping agrees with TransactionCategories.NormalizeOrOther input by input.
/// </summary>
public sealed class CategoryNormalizationSqlTests : IDisposable
{
    private static readonly Guid Couple = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid Plan1 = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Plan2 = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private readonly SqliteConnection _connection;

    public CategoryNormalizationSqlTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var ctx = new AppDbContext(options, coupleContext: null);
        ctx.Database.EnsureCreated();

        Execute("PRAGMA foreign_keys = OFF");
    }

    public void Dispose() => _connection.Dispose();

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private List<string?[]> Query(string sql, int columns)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string?[]>();
        while (reader.Read())
        {
            var row = new string?[columns];
            for (var i = 0; i < columns; i++)
                row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            rows.Add(row);
        }

        return rows;
    }

    private string SqlNormalize(string input)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT {CategoryNormalizationSql.Normalize("@v")}";
        command.Parameters.AddWithValue("@v", input);
        return (string)command.ExecuteScalar()!;
    }

    private void RunMigration()
    {
        foreach (var statement in CategoryNormalizationSql.UpStatements)
            Execute(statement);
    }

    private void InsertTransaction(string id, string category, string currency = "BRL")
        => Execute(
            "INSERT INTO transactions (id, couple_id, user_id, fingerprint, bank, amount, currency, event_timestamp_utc, category, ingest_event_id, created_at_utc, source) " +
            "VALUES (@id, @couple, @couple, @fp, 'NUBANK', 10, @currency, '2026-10-01 10:00:00', @category, @couple, '2026-10-01 10:00:00', 0)",
            ("@id", Guid.Parse(id)), ("@couple", Couple), ("@fp", "fp-" + id), ("@currency", currency), ("@category", category));

    private void InsertPlan(Guid id, string currency = "BRL")
        => Execute(
            "INSERT INTO budget_plans (id, couple_id, month, gross_income, currency, created_at_utc, updated_at_utc) " +
            "VALUES (@id, @couple, @month, 5000, @currency, '2026-10-01 10:00:00', '2026-10-01 10:00:00')",
            ("@id", id), ("@couple", Couple), ("@month", id == Plan1 ? "2026-10" : "2026-09"), ("@currency", currency));

    private void InsertAllocation(string id, Guid plan, string category, decimal amount, string created, string currency = "BRL")
        => Execute(
            "INSERT INTO budget_allocations (id, budget_plan_id, category, allocated_amount, currency, created_at_utc) " +
            "VALUES (@id, @plan, @category, @amount, @currency, @created)",
            ("@id", Guid.Parse(id)), ("@plan", plan), ("@category", category), ("@amount", amount), ("@currency", currency), ("@created", created));

    // ── the SQL mapping equals the C# rule ───────────────────────────────

    public static IEnumerable<object[]> MappingInputs()
    {
        var inputs = new List<string>
        {
            "", " ", "   ", "x", "Mercado", "Educação", "Vestuário", "Serviços", "Investimentos", "Food",
            "Alimentação extra", "Alimen tacao", "ALIMENTACAO1", "Alimentação/Mercado", "Outros2",
            "Alimentação", "Saúde", "ÁÉÍÓÚ ÀÈÌÒÙ ÂÊÎÔÛ ÃÕ ÄËÏÖÜ Ç Ñ", "ação", "ÇÃO",
        };

        foreach (var category in TransactionCategories.All)
        {
            foreach (var spelling in new[] { category.Key, category.Label })
            {
                inputs.Add(spelling);
                inputs.Add(spelling.ToLowerInvariant());
                inputs.Add(spelling.ToUpperInvariant());
                inputs.Add($"  {spelling}");
                inputs.Add($"{spelling}  ");
                inputs.Add($" {spelling.ToLowerInvariant()} ");
            }
        }

        return inputs.Distinct().Select(i => new object[] { i });
    }

    [Theory]
    [MemberData(nameof(MappingInputs))]
    public void SqlNormalize_MatchesTheCSharpRule(string input)
        => Assert.Equal(TransactionCategories.NormalizeOrOther(input), SqlNormalize(input));

    // ── transactions and rules ───────────────────────────────────────────

    [Fact]
    public void Migration_NormalizesTransactionAndRuleCategories()
    {
        InsertTransaction("00000000-0000-0000-0000-000000000e01", "Alimentação");
        InsertTransaction("00000000-0000-0000-0000-000000000e02", "alimentacao");
        InsertTransaction("00000000-0000-0000-0000-000000000e03", "  ALIMENTAÇÃO ");
        InsertTransaction("00000000-0000-0000-0000-000000000e04", "Saúde");
        InsertTransaction("00000000-0000-0000-0000-000000000e05", "Mercado");
        InsertTransaction("00000000-0000-0000-0000-000000000e06", "Educação");
        InsertTransaction("00000000-0000-0000-0000-000000000e07", "OUTROS");
        InsertTransaction("00000000-0000-0000-0000-000000000e08", "");
        Execute("INSERT INTO category_rules (id, keyword, category, priority, is_active) VALUES " +
                "('00000000-0000-0000-0000-000000000d01', 'IFOOD', 'Alimentação', 10, 1), " +
                "('00000000-0000-0000-0000-000000000d02', 'CINEMA', 'Lazer', 10, 1), " +
                "('00000000-0000-0000-0000-000000000d03', 'ESCOLA', 'Educação', 10, 1), " +
                "('00000000-0000-0000-0000-000000000d04', 'UBER', 'TRANSPORTE', 10, 1)");

        RunMigration();

        Assert.Equal(
            new[] { "ALIMENTACAO", "ALIMENTACAO", "ALIMENTACAO", "SAUDE", "OUTROS", "OUTROS", "OUTROS", "OUTROS" },
            Query("SELECT category FROM transactions ORDER BY id", 1).Select(r => r[0]));
        Assert.Equal(
            new[] { "ALIMENTACAO", "LAZER", "OUTROS", "TRANSPORTE" },
            Query("SELECT category FROM category_rules ORDER BY keyword", 1).Select(r => r[0]).OrderBy(c => c, StringComparer.Ordinal));

        // The backups keep the original spellings and currencies, one row per original row.
        Assert.Equal(
            new[] { "  ALIMENTAÇÃO ", "Alimentação", "Educação", "Mercado", "OUTROS", "Saúde", "alimentacao", "" }.OrderBy(c => c, StringComparer.Ordinal),
            Query("SELECT category FROM _backup_20261006_transactions_category", 1).Select(r => r[0]!).OrderBy(c => c, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "Alimentação", "Educação", "Lazer", "TRANSPORTE" }.OrderBy(c => c, StringComparer.Ordinal),
            Query("SELECT category FROM _backup_20261006_category_rules_category", 1).Select(r => r[0]!).OrderBy(c => c, StringComparer.Ordinal));
    }

    // ── allocations: collisions are summed ───────────────────────────────

    [Fact]
    public void Migration_SumsAllocationsOfTheSamePlanThatCollide()
    {
        InsertPlan(Plan1);
        InsertAllocation("00000000-0000-0000-0000-000000000b01", Plan1, "Alimentação", 1000.10m, "2026-10-01 10:00:00");
        InsertAllocation("00000000-0000-0000-0000-000000000b02", Plan1, "ALIMENTACAO", 500.20m, "2026-10-01 10:05:00");
        InsertAllocation("00000000-0000-0000-0000-000000000b03", Plan1, "alimentacao ", 250.00m, "2026-10-01 10:06:00");
        InsertAllocation("00000000-0000-0000-0000-000000000b04", Plan1, "Saúde", 300m, "2026-10-01 10:00:00");
        InsertAllocation("00000000-0000-0000-0000-000000000b05", Plan1, "Educação", 100m, "2026-10-01 10:00:00");
        InsertAllocation("00000000-0000-0000-0000-000000000b06", Plan1, "Mercado", 50m, "2026-10-01 10:00:00");
        InsertAllocation("00000000-0000-0000-0000-000000000b07", Plan1, "Outros", 25m, "2026-10-01 10:00:00");

        RunMigration();

        var rows = Query("SELECT category, allocated_amount FROM budget_allocations ORDER BY category", 2);
        Assert.Equal(
            new[] { ("ALIMENTACAO", 1750.3m), ("OUTROS", 175m), ("SAUDE", 300m) },
            rows.Select(r => (r[0]!, decimal.Parse(r[1]!, CultureInfo.InvariantCulture))));

        // Every original allocation is kept, including the ones merged away, with the original spelling and amount.
        var backup = Query("SELECT id, budget_plan_id, category, currency, allocated_amount FROM _backup_20261006_budget_allocations ORDER BY id", 5);
        Assert.Equal(
            new[]
            {
                ("00000000-0000-0000-0000-000000000b01", "Alimentação", 1000.10m),
                ("00000000-0000-0000-0000-000000000b02", "ALIMENTACAO", 500.20m),
                ("00000000-0000-0000-0000-000000000b03", "alimentacao ", 250.00m),
                ("00000000-0000-0000-0000-000000000b04", "Saúde", 300m),
                ("00000000-0000-0000-0000-000000000b05", "Educação", 100m),
                ("00000000-0000-0000-0000-000000000b06", "Mercado", 50m),
                ("00000000-0000-0000-0000-000000000b07", "Outros", 25m),
            },
            backup.Select(r => (r[0]!.ToLowerInvariant(), r[2]!, decimal.Parse(r[4]!, CultureInfo.InvariantCulture))));
        Assert.All(backup, r => Assert.Equal(Plan1.ToString(), r[1]!.ToLowerInvariant()));
    }

    [Fact]
    public void Migration_KeepsTheOldestAllocationOfAGroup_AndNeverMergesAcrossPlans()
    {
        InsertPlan(Plan1);
        InsertPlan(Plan2);
        InsertAllocation("00000000-0000-0000-0000-000000000b02", Plan1, "alimentacao", 20m, "2026-10-01 10:05:00");
        InsertAllocation("00000000-0000-0000-0000-000000000b01", Plan1, "Alimentação", 10m, "2026-10-01 10:00:00");
        InsertAllocation("00000000-0000-0000-0000-000000000b03", Plan2, "Alimentação", 7m, "2026-09-01 10:00:00");

        RunMigration();

        var rows = Query("SELECT id, category, allocated_amount FROM budget_allocations ORDER BY budget_plan_id", 3);
        Assert.Equal(2, rows.Count);
        Assert.Equal("00000000-0000-0000-0000-000000000b01", rows[0][0]!.ToLowerInvariant());
        Assert.Equal("ALIMENTACAO", rows[0][1]);
        Assert.Equal(30m, decimal.Parse(rows[0][2]!, CultureInfo.InvariantCulture));
        Assert.Equal(7m, decimal.Parse(rows[1][2]!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Migration_NeverSumsAllocationsOfDifferentCurrencies()
    {
        InsertPlan(Plan1);
        InsertAllocation("00000000-0000-0000-0000-000000000b01", Plan1, "Lazer", 10m, "2026-10-01 10:00:00", "BRL");
        InsertAllocation("00000000-0000-0000-0000-000000000b02", Plan1, "LAZER", 5m, "2026-10-01 10:01:00", "brl");   // same currency once BRL is unified
        InsertAllocation("00000000-0000-0000-0000-000000000b03", Plan1, "lazer", 100m, "2026-10-01 10:02:00", "USD"); // never merged with BRL

        RunMigration();

        var rows = Query("SELECT category, allocated_amount, currency FROM budget_allocations ORDER BY currency", 3);
        Assert.Equal(2, rows.Count);
        Assert.Equal(("LAZER", 15m, "BRL"), (rows[0][0]!, decimal.Parse(rows[0][1]!, CultureInfo.InvariantCulture), rows[0][2]!));
        Assert.Equal(("LAZER", 100m, "USD"), (rows[1][0]!, decimal.Parse(rows[1][1]!, CultureInfo.InvariantCulture), rows[1][2]!));
    }

    [Fact]
    public void Migration_TieOnCreationTime_StillKeepsExactlyOneRow()
    {
        InsertPlan(Plan1);
        InsertAllocation("00000000-0000-0000-0000-000000000b01", Plan1, "Lazer", 1m, "2026-10-01 10:00:00");
        InsertAllocation("00000000-0000-0000-0000-000000000b02", Plan1, "LAZER", 2m, "2026-10-01 10:00:00");
        InsertAllocation("00000000-0000-0000-0000-000000000b03", Plan1, "lazer", 4m, "2026-10-01 10:00:00");

        RunMigration();

        var row = Assert.Single(Query("SELECT category, allocated_amount FROM budget_allocations", 2));
        Assert.Equal("LAZER", row[0]);
        Assert.Equal(7m, decimal.Parse(row[1]!, CultureInfo.InvariantCulture));
    }

    // ── currency and safety ──────────────────────────────────────────────

    [Fact]
    public void Migration_UnifiesBrlSpelling_AndLeavesOtherCurrenciesAlone()
    {
        InsertPlan(Plan1, "brl");
        InsertPlan(Plan2, "USD");
        InsertAllocation("00000000-0000-0000-0000-000000000b01", Plan1, "Lazer", 1m, "2026-10-01 10:00:00", "Brl");
        InsertTransaction("00000000-0000-0000-0000-000000000e01", "Lazer", "brl");
        InsertTransaction("00000000-0000-0000-0000-000000000e02", "Lazer", "USD");
        InsertTransaction("00000000-0000-0000-0000-000000000e03", "Lazer", "EUR");

        RunMigration();

        Assert.Equal(new[] { "BRL", "USD" }, Query("SELECT currency FROM budget_plans ORDER BY currency", 1).Select(r => r[0]));
        Assert.Equal(new[] { "BRL" }, Query("SELECT currency FROM budget_allocations", 1).Select(r => r[0]));
        Assert.Equal(new[] { "BRL", "EUR", "USD" }, Query("SELECT currency FROM transactions ORDER BY currency", 1).Select(r => r[0]));
        // amounts of foreign-currency rows are untouched
        Assert.Equal(new[] { "10", "10", "10" }, Query("SELECT amount FROM transactions", 1).Select(r => r[0]));
    }

    [Fact]
    public void Migration_IsIdempotent_AndLeavesCanonicalDataUntouched()
    {
        InsertPlan(Plan1);
        InsertAllocation("00000000-0000-0000-0000-000000000b01", Plan1, "ALIMENTACAO", 10m, "2026-10-01 10:00:00");
        InsertAllocation("00000000-0000-0000-0000-000000000b02", Plan1, "LAZER", 20m, "2026-10-01 10:00:00");
        InsertTransaction("00000000-0000-0000-0000-000000000e01", "SAUDE");

        RunMigration();
        var first = Query("SELECT id, category, allocated_amount FROM budget_allocations ORDER BY id", 3);
        RunMigration();
        var second = Query("SELECT id, category, allocated_amount FROM budget_allocations ORDER BY id", 3);

        Assert.Equal(2, first.Count);
        Assert.Equal(first.Select(r => string.Join('|', r)), second.Select(r => string.Join('|', r)));
        Assert.Equal("SAUDE", Query("SELECT category FROM transactions", 1).Single()[0]);
    }

    [Fact]
    public void Migration_RunTwice_KeepsTheFirstBackup_NotTheNormalizedData()
    {
        InsertPlan(Plan1);
        InsertAllocation("00000000-0000-0000-0000-000000000b01", Plan1, "Lazer", 1m, "2026-10-01 10:00:00", "brl");
        InsertAllocation("00000000-0000-0000-0000-000000000b02", Plan1, "LAZER", 2m, "2026-10-01 10:05:00");
        InsertTransaction("00000000-0000-0000-0000-000000000e01", "Saúde", "brl");

        RunMigration();
        RunMigration();

        Assert.Equal(new[] { ("Saúde", "brl") }, Query("SELECT category, currency FROM _backup_20261006_transactions_category", 2).Select(r => (r[0]!, r[1]!)));
        Assert.Equal(
            new[] { ("Lazer", "brl", 1m), ("LAZER", "BRL", 2m) },
            Query("SELECT category, currency, allocated_amount FROM _backup_20261006_budget_allocations ORDER BY id", 3)
                .Select(r => (r[0]!, r[1]!, decimal.Parse(r[2]!, CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void Migration_OnEmptyTables_DoesNothing()
        => RunMigration();

    [Fact]
    public void EveryStatement_OnlyUsesPortableSqlFunctions()
    {
        // The migration text is shared by PostgreSQL and these tests: no vendor-specific function sneaks in.
        var all = string.Join('\n', CategoryNormalizationSql.UpStatements).ToUpperInvariant();
        foreach (var vendorOnly in new[] { "TRANSLATE(", "UNACCENT(", "ILIKE", "REGEXP_REPLACE", "ARRAY_AGG", "CHR(", "::" })
            Assert.DoesNotContain(vendorOnly, all);
    }
}
