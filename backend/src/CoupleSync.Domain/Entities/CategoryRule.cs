namespace CoupleSync.Domain.Entities;

public sealed class CategoryRule
{
    private CategoryRule() { }

    public CategoryRule(Guid id, string keyword, string category, int priority, bool isActive)
    {
        Id = id;
        Keyword = keyword;
        Category = category;
        Priority = priority;
        IsActive = isActive;
    }

    public Guid Id { get; private set; }
    public string Keyword { get; private set; } = string.Empty;
    public string Category { get; private set; } = string.Empty;
    public int Priority { get; private set; }
    public bool IsActive { get; private set; } = true;
}
