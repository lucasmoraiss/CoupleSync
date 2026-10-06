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
    // (accented, plain) pairs, lower and upper case, plus the combining marks of decomposed text.
    private static readonly (string From, string To)[] AccentFolds =
    [
        ("á", "a"), ("à", "a"), ("â", "a"), ("ã", "a"), ("ä", "a"),
        ("Á", "A"), ("À", "A"), ("Â", "A"), ("Ã", "A"), ("Ä", "A"),
        ("é", "e"), ("è", "e"), ("ê", "e"), ("ë", "e"),
        ("É", "E"), ("È", "E"), ("Ê", "E"), ("Ë", "E"),
        ("í", "i"), ("ì", "i"), ("î", "i"), ("ï", "i"),
        ("Í", "I"), ("Ì", "I"), ("Î", "I"), ("Ï", "I"),
        ("ó", "o"), ("ò", "o"), ("ô", "o"), ("õ", "o"), ("ö", "o"),
        ("Ó", "O"), ("Ò", "O"), ("Ô", "O"), ("Õ", "O"), ("Ö", "O"),
        ("ú", "u"), ("ù", "u"), ("û", "u"), ("ü", "u"),
        ("Ú", "U"), ("Ù", "U"), ("Û", "U"), ("Ü", "U"),
        ("ç", "c"), ("Ç", "C"), ("ñ", "n"), ("Ñ", "N"),
        ("̀", ""), ("́", ""), ("̂", ""), ("̃", ""), ("̈", ""), ("̧", ""),
    ];

    // SQLite refuses REPLACE calls nested ~50 deep ("parser stack overflow"), so the chain is cut into
    // layers of at most this many calls, each one a derived table over the previous one.
    private const int ReplacesPerLayer = 12;

    /// <summary>The folded key of a column: accents removed, trimmed, upper case.</summary>
    public static string Fold(string column)
    {
        var source = $"(SELECT {column} AS v)";
        foreach (var layer in AccentFolds.Chunk(ReplacesPerLayer))
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

    /// <summary>Statements run by the migration, in order.</summary>
    public static IReadOnlyList<string> UpStatements { get; } = Build();

    private static List<string> Build()
    {
        var statements = new List<string>
        {
            // transactions and category rules: variants of case/accents converge, the rest becomes OUTROS.
            $"UPDATE transactions SET category = {Normalize("category")} WHERE category <> {Normalize("category")}",
            $"UPDATE category_rules SET category = {Normalize("category")} WHERE category <> {Normalize("category")}",
        };

        // budget_allocations: allocations of one plan whose categories now collide are summed into one.
        // The survivor of a group is its oldest row (created_at_utc, then id).
        const string sameGroup =
            "o.budget_plan_id = a.budget_plan_id AND o.id <> a.id AND {0} = {1}";
        var groupMatch = string.Format(sameGroup, Normalize("o.category"), Normalize("a.category"));
        var olderInGroup = $"{groupMatch} AND (o.created_at_utc < a.created_at_utc OR (o.created_at_utc = a.created_at_utc AND o.id < a.id))";

        statements.Add(
            "UPDATE budget_allocations AS a SET allocated_amount = (" +
            "SELECT SUM(g.allocated_amount) FROM budget_allocations AS g " +
            $"WHERE g.budget_plan_id = a.budget_plan_id AND {Normalize("g.category")} = {Normalize("a.category")}) " +
            $"WHERE EXISTS (SELECT 1 FROM budget_allocations AS o WHERE {groupMatch}) " +
            $"AND NOT EXISTS (SELECT 1 FROM budget_allocations AS o WHERE {olderInGroup})");

        statements.Add(
            "DELETE FROM budget_allocations AS a " +
            $"WHERE EXISTS (SELECT 1 FROM budget_allocations AS o WHERE {olderInGroup})");

        statements.Add(
            $"UPDATE budget_allocations SET category = {Normalize("category")} WHERE category <> {Normalize("category")}");

        // Currency: only the spelling of BRL is unified ("brl", " BRL"). Other currencies stay as they are.
        foreach (var table in new[] { "transactions", "budget_plans", "budget_allocations", "goals", "income_sources" })
        {
            statements.Add(
                $"UPDATE {table} SET currency = '{BrlCurrency}' " +
                $"WHERE currency <> '{BrlCurrency}' AND UPPER(TRIM(currency)) = '{BrlCurrency}'");
        }

        return statements;
    }
}
