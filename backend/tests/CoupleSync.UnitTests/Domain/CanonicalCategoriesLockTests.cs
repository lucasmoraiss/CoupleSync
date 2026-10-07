using System.Text.RegularExpressions;
using CoupleSync.Api.Controllers;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Integrations.Gemini;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.UnitTests.Domain;

/// <summary>
/// The API owns the canonical category list (GET /api/v1/categories); the app keeps a copy as its offline
/// fallback (mobile/src/modules/transactions/categories.ts). These tests lock the two copies together and
/// make sure every category the backend produces on its own is in the list.
/// </summary>
public sealed class CanonicalCategoriesLockTests
{
    private static readonly Regex Entry = new(
        @"\{\s*value:\s*'(?<key>[^']+)'\s*,\s*label:\s*'(?<label>[^']+)'",
        RegexOptions.Compiled);

    private static string FindInRepository(params string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. relativePath]);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(Path.Combine(relativePath) + " not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void MobileFallbackList_EqualsTheApiList_KeysLabelsAndOrder()
    {
        var source = File.ReadAllText(FindInRepository("mobile", "src", "modules", "transactions", "categories.ts"));

        var mobile = Entry.Matches(source)
            .Select(m => (m.Groups["key"].Value, m.Groups["label"].Value))
            .ToList();

        var api = TransactionCategories.All.Select(c => (c.Key, c.Label)).ToList();

        Assert.Equal(api, mobile);
    }

    [Fact]
    public void CategoriesRoute_ReturnsTheCanonicalList()
    {
        var result = new CategoriesController().GetCategories();

        var body = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<CoupleSync.Api.Contracts.Categories.CategoriesResponse>(body.Value);
        Assert.Equal(
            TransactionCategories.All.Select(c => (c.Key, c.Label)),
            response.Categories.Select(c => (c.Key, c.Label)));
    }

    [Fact]
    public void SeededCategoryRules_AreAllCanonicalCategories()
    {
        var json = File.ReadAllText(FindInRepository(
            "backend", "src", "CoupleSync.Infrastructure", "Persistence", "Seeders", "category-rules.json"));
        var categories = Regex.Matches(json, "\"category\"\\s*:\\s*\"(?<c>[^\"]+)\"").Select(m => m.Groups["c"].Value).Distinct();

        Assert.All(categories, c => Assert.NotNull(TransactionCategories.TryNormalize(c)));
    }

    [Fact]
    public void ClassifierSuggestionLists_OnlyOfferCanonicalLabels()
    {
        Assert.All(GeminiCategoryClassifier.DefaultCategories, c => Assert.NotNull(TransactionCategories.TryNormalize(c)));
        Assert.Equal(TransactionCategories.All.Count, GeminiCategoryClassifier.DefaultCategories.Length);
    }
}
