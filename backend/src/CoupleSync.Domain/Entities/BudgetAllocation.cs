namespace CoupleSync.Domain.Entities;

// BudgetAllocation does NOT implement ICoupleScoped — couple isolation is enforced
// transitively through BudgetPlan.CoupleId + the global query filter.
// BudgetAllocations has no public DbSet; access is only via BudgetPlan.Allocations.
// scope field deferred to V2 per FR-118
public sealed class BudgetAllocation
{
    private BudgetAllocation() { }

    private BudgetAllocation(
        Guid id,
        Guid budgetPlanId,
        string category,
        decimal allocatedAmount,
        string currency,
        DateTime createdAtUtc)
    {
        Id = id;
        BudgetPlanId = budgetPlanId;
        Category = category;
        AllocatedAmount = allocatedAmount;
        Currency = currency;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid BudgetPlanId { get; private set; }
    public string Category { get; private set; } = string.Empty;
    public decimal AllocatedAmount { get; private set; }
    public string Currency { get; private set; } = "BRL";
    public DateTime CreatedAtUtc { get; private set; }

    public BudgetPlan BudgetPlan { get; private set; } = null!;

    public static BudgetAllocation Create(
        Guid budgetPlanId,
        string category,
        decimal allocatedAmount,
        string currency,
        DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(category) || category.Length > 64)
            throw new ArgumentException("A categoria é obrigatória e deve ter no máximo 64 caracteres.", nameof(category));

        if (allocatedAmount < 0)
            throw new ArgumentException("O valor alocado não pode ser negativo.", nameof(allocatedAmount));

        if (string.IsNullOrWhiteSpace(currency) || currency.Length < 2 || currency.Length > 3)
            throw new ArgumentException("A moeda deve ter de 2 a 3 caracteres.", nameof(currency));

        if (createdAtUtc.Kind == DateTimeKind.Unspecified)
            createdAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);

        return new BudgetAllocation(Guid.NewGuid(), budgetPlanId, category, allocatedAmount, currency, createdAtUtc);
    }
}
