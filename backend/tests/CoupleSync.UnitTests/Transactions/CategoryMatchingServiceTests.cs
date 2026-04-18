using CoupleSync.Domain.Entities;
using CoupleSync.Infrastructure.Security;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Transactions;

public sealed class CategoryMatchingServiceTests
{
    private static CategoryMatchingService BuildService(params (string keyword, string category, int priority)[] rules)
    {
        var repo = new FakeCategoryRuleRepository();
        foreach (var (keyword, category, priority) in rules)
        {
            repo.Rules.Add(new CategoryRule(Guid.NewGuid(), keyword, category, priority, true));
        }

        return new CategoryMatchingService(repo);
    }

    [Fact]
    public async Task MatchCategoryAsync_KeywordInMerchant_ReturnsCorrectCategory()
    {
        var service = BuildService(("IFOOD", "Alimentação", 10));

        var result = await service.MatchCategoryAsync(null, "IFOOD DELIVERY", CancellationToken.None);

        Assert.Equal("Alimentação", result);
    }

    [Fact]
    public async Task MatchCategoryAsync_KeywordInDescription_ReturnsCorrectCategory()
    {
        var service = BuildService(("NETFLIX", "Lazer", 10));

        var result = await service.MatchCategoryAsync("NETFLIX monthly subscription", null, CancellationToken.None);

        Assert.Equal("Lazer", result);
    }

    [Fact]
    public async Task MatchCategoryAsync_NoMatch_ReturnsOutros()
    {
        var service = BuildService(("IFOOD", "Alimentação", 10));

        var result = await service.MatchCategoryAsync("Random Store", "Unknown Merchant", CancellationToken.None);

        Assert.Equal("OUTROS", result);
    }

    [Fact]
    public async Task MatchCategoryAsync_PriorityOrdering_HigherPriorityWins()
    {
        // UBER EATS (priority 20) should win over UBER (priority 10)
        var service = BuildService(
            ("UBER", "Transporte", 10),
            ("UBER EATS", "Alimentação", 20));

        var result = await service.MatchCategoryAsync(null, "UBER EATS delivery", CancellationToken.None);

        Assert.Equal("Alimentação", result);
    }

    [Fact]
    public async Task MatchCategoryAsync_CaseInsensitive_MatchesLowercase()
    {
        var service = BuildService(("IFOOD", "Alimentação", 10));

        var result = await service.MatchCategoryAsync(null, "ifood delivery", CancellationToken.None);

        Assert.Equal("Alimentação", result);
    }

    [Fact]
    public async Task MatchCategoryAsync_NullDescriptionAndMerchant_ReturnsOutros()
    {
        var service = BuildService(("IFOOD", "Alimentação", 10));

        var result = await service.MatchCategoryAsync(null, null, CancellationToken.None);

        Assert.Equal("OUTROS", result);
    }
}
