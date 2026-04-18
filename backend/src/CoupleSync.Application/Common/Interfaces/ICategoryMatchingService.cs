namespace CoupleSync.Application.Common.Interfaces;

public interface ICategoryMatchingService
{
    Task<string> MatchCategoryAsync(string? description, string? merchant, CancellationToken ct);
}
