using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Application.OcrImport;

public sealed class OcrProcessingService
{
    private readonly ITransactionRepository _transactionRepository;
    private readonly ICategoryClassifier _categoryClassifier;
    private readonly IBudgetRepository _budgetRepository;

    private static readonly TimeSpan BatchClassificationTimeout = TimeSpan.FromSeconds(15);

    internal static readonly string[] DefaultCategories =
        TransactionCategories.All.Select(c => c.Label).ToArray();

    public OcrProcessingService(
        ITransactionRepository transactionRepository,
        ICategoryClassifier categoryClassifier,
        IBudgetRepository budgetRepository)
    {
        _transactionRepository = transactionRepository;
        _categoryClassifier = categoryClassifier;
        _budgetRepository = budgetRepository;
    }

    /// <summary>
    /// Parses raw Azure Document Intelligence JSON into OcrCandidate list,
    /// sanitizes fields, computes dedup fingerprints, flags duplicates,
    /// and populates SuggestedCategory via AI classifier.
    /// </summary>
    public async Task<IReadOnlyList<OcrCandidate>> ParseAndDeduplicateAsync(
        Guid coupleId,
        string rawOcrJson,
        CancellationToken ct,
        bool aiCategorizationConsent = false)
    {
        var candidates = ParseCandidates(rawOcrJson);

        foreach (var c in candidates)
        {
            c.Description = SanitizeDescription(c.Description);
            c.Amount = Math.Abs(c.Amount);
        }

        // Credits (entradas, bill payments) are kept so the review can say they were not imported, but
        // they are never importable: they get no fingerprint, no category, and come after every debit so
        // the debit indices stay 0..n-1 exactly as before.
        var credits = candidates.Where(c => c.Type == TransactionType.Credit).ToList();
        candidates.RemoveAll(c => c.Type == TransactionType.Credit);

        for (int i = 0; i < candidates.Count; i++)
            candidates[i].Index = i;
        for (int i = 0; i < credits.Count; i++)
            credits[i].Index = candidates.Count + i;

        // Identical lines inside one statement are distinct purchases (e.g. two rides of the same
        // price on the same day). The n-th repetition gets an occurrence ordinal in its fingerprint;
        // statement order is deterministic, so re-importing the same file yields the same values.
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            var baseFingerprint = ComputeFingerprint(coupleId, c.Date, c.Amount, c.Description);
            var occurrence = occurrences.GetValueOrDefault(baseFingerprint) + 1;
            occurrences[baseFingerprint] = occurrence;

            c.Fingerprint = occurrence == 1
                ? baseFingerprint
                : ComputeFingerprint(coupleId, c.Date, c.Amount, c.Description, occurrence);
            c.DuplicateSuspected = await _transactionRepository.FingerprintExistsAsync(c.Fingerprint, coupleId, ct);
        }

        // Descriptions only go to the AI classifier (Gemini) when the uploader accepted the AI disclosure.
        if (aiCategorizationConsent)
            await ClassifyCandidatesAsync(coupleId, candidates, ct);

        candidates.AddRange(credits);
        return candidates;
    }

    /// <summary>Serializes candidates list to JSON for storage in ImportJob.OcrResultJson.</summary>
    public static string SerializeCandidates(IReadOnlyList<OcrCandidate> candidates) =>
        JsonSerializer.Serialize(candidates);

    /// <summary>
    /// Calls the AI classifier for each candidate with a 15-second batch timeout.
    /// Populates <see cref="OcrCandidate.SuggestedCategory"/>; never throws.
    /// </summary>
    private async Task ClassifyCandidatesAsync(
        Guid coupleId,
        List<OcrCandidate> candidates,
        CancellationToken ct)
    {
        var categories = await GetAvailableCategoriesAsync(coupleId, ct);

        using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        batchCts.CancelAfter(BatchClassificationTimeout);

        foreach (var candidate in candidates)
        {
            if (batchCts.IsCancellationRequested) break;

            var suggested = await _categoryClassifier.SuggestCategoryAsync(
                candidate.Description, categories, batchCts.Token);

            // Only canonical categories leave here; anything else the classifier invents is dropped.
            candidate.SuggestedCategory = TransactionCategories.TryNormalize(suggested);
        }
    }

    private async Task<IReadOnlyList<string>> GetAvailableCategoriesAsync(Guid coupleId, CancellationToken ct)
    {
        var currentMonth = BrazilTime.MonthOf(DateTime.UtcNow);
        var plan = await _budgetRepository.GetByMonthAsync(coupleId, currentMonth, ct);

        if (plan?.Allocations.Count > 0)
        {
            var categories = plan.Allocations
                .Select(a => TransactionCategories.TryNormalize(a.Category))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Select(TransactionCategories.Label)
                .ToList();

            if (categories.Count > 0)
                return categories;
        }

        return DefaultCategories;
    }



    public static List<OcrCandidate> ParseCandidates(string rawJson)
    {
        var candidates = new List<OcrCandidate>();

        using var doc = JsonDocument.Parse(rawJson);

        // Route to local-pdf branch when discriminator is present
        if (doc.RootElement.TryGetProperty("provider", out var providerEl)
            && string.Equals(providerEl.GetString(), "local-pdf", StringComparison.Ordinal))
        {
            return ParseLocalPdfCandidates(doc.RootElement);
        }

        // ── Existing Azure Document Intelligence format ──────────────────────
        if (!doc.RootElement.TryGetProperty("analyzeResult", out var analyzeResult))
            return candidates;
        if (!analyzeResult.TryGetProperty("documents", out var documents))
            return candidates;

        foreach (var document in documents.EnumerateArray())
        {
            if (!document.TryGetProperty("fields", out var fields))
                continue;

            var transactionDate = DateTime.UtcNow;

            if (fields.TryGetProperty("TransactionDate", out var dateField)
                && dateField.TryGetProperty("valueDate", out var vDate))
            {
                var dateStr = vDate.GetString();
                if (!string.IsNullOrEmpty(dateStr)
                    && DateTime.TryParse(dateStr, out var parsed))
                {
                    transactionDate = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                }
            }

            if (!fields.TryGetProperty("Items", out var items))
                continue;
            if (!items.TryGetProperty("valueArray", out var itemArray))
                continue;

            var index = 0;
            foreach (var item in itemArray.EnumerateArray())
            {
                if (!item.TryGetProperty("valueObject", out var obj))
                    continue;

                string description = string.Empty;
                decimal amount = 0m;
                string currency = "BRL";
                double confidence = 0.0;

                if (obj.TryGetProperty("Description", out var descField))
                {
                    if (descField.TryGetProperty("valueString", out var descVal))
                        description = descVal.GetString() ?? string.Empty;

                    if (descField.TryGetProperty("confidence", out var confVal))
                        confidence = confVal.GetDouble();
                }

                if (obj.TryGetProperty("TotalPrice", out var priceField)
                    && priceField.TryGetProperty("valueCurrency", out var currField))
                {
                    if (currField.TryGetProperty("amount", out var amountVal))
                        amount = amountVal.GetDecimal();
                    if (currField.TryGetProperty("currencyCode", out var currCode))
                        currency = currCode.GetString() ?? "BRL";
                }

                candidates.Add(new OcrCandidate
                {
                    Index = index++,
                    Date = transactionDate,
                    Description = description,
                    Amount = amount,
                    Currency = currency,
                    Confidence = confidence,
                });
            }
        }

        return candidates;
    }

    public static string SanitizeDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "Transação importada";

        // Remove control characters (C0, DEL)
        var sanitized = Regex.Replace(description, @"[\x00-\x1F\x7F]", string.Empty);
        sanitized = Regex.Replace(sanitized.Trim(), @"\s+", " ");

        if (sanitized.Length > 200)
            sanitized = sanitized[..200];

        return sanitized;
    }

    /// <summary>
    /// Dedup fingerprint of a statement line: couple | date | amount | description, plus the
    /// occurrence ordinal for the 2nd, 3rd... identical line of the same statement.
    /// </summary>
    public static string ComputeFingerprint(Guid coupleId, DateTime date, decimal amount, string description, int occurrence = 1)
    {
        // The first occurrence keeps the original format so that transactions imported before the
        // ordinal existed are still recognised as duplicates on re-import.
        var normalized = $"{coupleId}|{date:yyyy-MM-dd}|{amount:F2}|{description.ToLowerInvariant().Trim()}";
        if (occurrence > 1)
            normalized += $"|#{occurrence}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ── local-pdf discriminator branch ──────────────────────────────────────

    private static List<OcrCandidate> ParseLocalPdfCandidates(JsonElement root)
    {
        var candidates = new List<OcrCandidate>();

        if (!root.TryGetProperty("transactions", out var txArray))
            return candidates;

        var index = 0;
        foreach (var tx in txArray.EnumerateArray())
        {
            var description = tx.TryGetProperty("description", out var descEl)
                ? descEl.GetString() ?? string.Empty
                : string.Empty;

            DateTime date = DateTime.UtcNow;
            if (tx.TryGetProperty("date", out var dateEl)
                && DateTime.TryParse(dateEl.GetString(), out var parsedDate))
            {
                date = DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc);
            }

            decimal amount = 0m;
            if (tx.TryGetProperty("amount", out var amtEl))
                amount = amtEl.GetDecimal();

            TransactionType? type = null;
            if (tx.TryGetProperty("type", out var typeEl))
            {
                var typeStr = typeEl.GetString();
                if (Enum.TryParse<TransactionType>(typeStr, ignoreCase: true, out var parsedType))
                    type = parsedType;
            }

            candidates.Add(new OcrCandidate
            {
                Index = index++,
                Date = date,
                Description = description,
                Amount = amount,
                Currency = "BRL",
                Confidence = 1.0, // deterministic local parser
                Type = type,
            });
        }

        return candidates;
    }
}
