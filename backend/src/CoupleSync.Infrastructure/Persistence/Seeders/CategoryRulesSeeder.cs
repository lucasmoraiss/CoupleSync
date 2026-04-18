using System.Text.Json;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence.Seeders;

public sealed class CategoryRulesSeeder
{
    private readonly AppDbContext _dbContext;

    public CategoryRulesSeeder(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var json = await LoadJsonAsync();
        var entries = JsonSerializer.Deserialize<List<CategoryRuleEntry>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize category-rules.json");

        var existingKeywords = await _dbContext.CategoryRules
            .Select(r => r.Keyword)
            .ToListAsync(ct);

        var existingSet = new HashSet<string>(existingKeywords, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (existingSet.Contains(entry.Keyword))
                continue;

            var rule = new CategoryRule(Guid.NewGuid(), entry.Keyword, entry.Category, entry.Priority, true);
            await _dbContext.CategoryRules.AddAsync(rule, ct);
            existingSet.Add(entry.Keyword);
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    private static async Task<string> LoadJsonAsync()
    {
        var assembly = typeof(CategoryRulesSeeder).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("category-rules.json", StringComparison.OrdinalIgnoreCase));

        if (resourceName is not null)
        {
            await using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }

        // Fallback: load from file alongside DLL
        var dir = Path.GetDirectoryName(assembly.Location)!;
        var filePath = Path.Combine(dir, "Persistence", "Seeders", "category-rules.json");
        if (!File.Exists(filePath))
        {
            filePath = Path.Combine(dir, "category-rules.json");
        }

        return await File.ReadAllTextAsync(filePath);
    }

    private sealed record CategoryRuleEntry(string Keyword, string Category, int Priority);
}
