using System.Text;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Infrastructure.Persistence;

/// <summary>
/// SQL used by the NormalizeCategoriesAndCurrency migration. It only relies on TRIM, REPLACE, UPPER,
/// CASE, correlated sub-queries and uuid ordering, which PostgreSQL (production) and SQLite (unit
/// tests) both support, so the exact statements that run in production are exercised by tests and
/// compared, input by input, with <see cref="TransactionCategories.NormalizeOrOther"/>.
/// Known difference from the C# rule: only spaces are trimmed (C# also trims tabs and line breaks).
/// </summary>
public static class CategoryNormalizationSql
{
    // SQLite refuses REPLACE calls nested ~50 deep ("parser stack overflow"), so the chain is cut into
    // layers of at most this many calls, each one a derived table over the previous one.
    private const int ReplacesPerLayer = 12;

    /// <summary>The folded key of a column: accents removed, trimmed, upper case.</summary>
    public static string Fold(string column)
    {
        var source = $"(SELECT {column} AS v)";
        foreach (var layer in AccentFolding.Pairs.Chunk(ReplacesPerLayer))
        {
            var expression = "v";
            foreach (var (from, to) in layer)
                expression = $"REPLACE({expression}, '{from}', '{to}')";

            source = $"(SELECT {expression} AS v FROM {source} AS s)";
        }

        return $"(SELECT UPPER(TRIM(v)) FROM {source} AS f)";
    }

    /// <summary>The canonical key for a column: a known key stays, everything else becomes OUTROS.</summary>
    public static string Normalize(string column)
    {
        var sb = new StringBuilder("CASE ").Append(Fold(column));
        foreach (var category in TransactionCategories.All)
            sb.Append($" WHEN '{category.Key}' THEN '{category.Key}'");

        return sb.Append($" ELSE '{TransactionCategories.Other}' END").ToString();
    }

    public const string BrlCurrency = CurrencyRules.Brl;

    /// <summary>
    /// Copies of the values this migration rewrites, taken before anything is changed. Plain tables outside the EF
    /// model (not in the snapshot). IF NOT EXISTS keeps the first copy when the statements run again.
    /// </summary>
    public static IReadOnlyList<string> BackupStatements { get; } = new[]
    {
        "CREATE TABLE IF NOT EXISTS _backup_20261006_transactions_category AS SELECT id, category, currency FROM transactions",
        "CREATE TABLE IF NOT EXISTS _backup_20261006_category_rules_category AS SELECT id, category FROM category_rules",
        "CREATE TABLE IF NOT EXISTS _backup_20261006_budget_allocations AS " +
        "SELECT id, budget_plan_id, category, currency, allocated_amount FROM budget_allocations",
    };

    /// <summary>Statements run by the migration, in order.</summary>
    public static IReadOnlyList<string> UpStatements { get; } = Build();

    private static List<string> Build()
    {
        // The backups come first: nothing is rewritten before the original values are kept.
        var statements = new List<string>(BackupStatements);

        // Currency: only the spelling of BRL is unified ("brl", " BRL"). Other currencies stay as they are.
        // Runs first so that "brl" and "BRL" allocations are the same currency when collisions are merged below.
        foreach (var table in new[] { "transactions", "budget_plans", "budget_allocations", "goals", "income_sources" })
        {
            statements.Add(
                $"UPDATE {table} SET currency = '{BrlCurrency}' " +
                $"WHERE currency <> '{BrlCurrency}' AND UPPER(TRIM(currency)) = '{BrlCurrency}'");
        }

        statements.AddRange(new[]
        {
            // transactions and category rules: variants of case/accents converge, the rest becomes OUTROS.
            $"UPDATE transactions SET category = {Normalize("category")} WHERE category <> {Normalize("category")}",
            $"UPDATE category_rules SET category = {Normalize("category")} WHERE category <> {Normalize("category")}",
        });

        // budget_allocations: allocations of one plan whose categories now collide are summed into one.
        // The survivor of a group is its oldest row (created_at_utc, then id).
        const string sameGroup =
            "o.budget_plan_id = a.budget_plan_id AND o.currency = a.currency AND o.id <> a.id AND {0} = {1}";
        var groupMatch = string.Format(sameGroup, Normalize("o.category"), Normalize("a.category"));
        var olderInGroup = $"{groupMatch} AND (o.created_at_utc < a.created_at_utc OR (o.created_at_utc = a.created_at_utc AND o.id < a.id))";

        statements.Add(
            "UPDATE budget_allocations AS a SET allocated_amount = (" +
            "SELECT SUM(g.allocated_amount) FROM budget_allocations AS g " +
            $"WHERE g.budget_plan_id = a.budget_plan_id AND g.currency = a.currency AND {Normalize("g.category")} = {Normalize("a.category")}) " +
            $"WHERE EXISTS (SELECT 1 FROM budget_allocations AS o WHERE {groupMatch}) " +
            $"AND NOT EXISTS (SELECT 1 FROM budget_allocations AS o WHERE {olderInGroup})");

        statements.Add(
            "DELETE FROM budget_allocations AS a " +
            $"WHERE EXISTS (SELECT 1 FROM budget_allocations AS o WHERE {olderInGroup})");

        statements.Add(
            $"UPDATE budget_allocations SET category = {Normalize("category")} WHERE category <> {Normalize("category")}");

        return statements;
    }
}
