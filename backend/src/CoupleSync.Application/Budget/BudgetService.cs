using CoupleSync.Application.Budget.Commands;
using CoupleSync.Application.Budget.Queries;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Application.Budget;

public sealed class BudgetService
{
    private readonly IBudgetRepository _repository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public BudgetService(
        IBudgetRepository repository,
        ITransactionRepository transactionRepository,
        IDateTimeProvider dateTimeProvider)
    {
        _repository = repository;
        _transactionRepository = transactionRepository;
        _dateTimeProvider = dateTimeProvider;
    }

    /// <summary>
    /// Creates or updates a budget plan for the given couple and month (upsert by couple_id + month).
    /// Throws ConflictException on concurrent update collision.
    /// </summary>
    public async Task<BudgetPlanDto> UpsertPlanAsync(
        Guid coupleId,
        string month,
        decimal grossIncome,
        string currency,
        CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;

        var existing = await _repository.GetByMonthAsync(coupleId, month, cancellationToken);

        try
        {
            BudgetPlan plan;
            if (existing is null)
            {
                plan = BudgetPlan.Create(coupleId, month, grossIncome, currency, now);
                await _repository.AddAsync(plan, cancellationToken);
            }
            else
            {
                existing.Update(grossIncome, currency, now);
                plan = existing;
            }

            await _repository.SaveChangesAsync(cancellationToken);
            return MapToDto(plan);
        }
        catch (ConcurrencyConflictException)
        {
            throw new ConflictException(
                "BUDGET_PLAN_CONFLICT",
                "O orçamento foi alterado ao mesmo tempo por outra requisição. Tente novamente.");
        }
        catch (UniqueViolationException)
        {
            throw new ConflictException(
                "BUDGET_PLAN_CONFLICT",
                "Já existe um orçamento para este mês. Tente novamente.");
        }
    }

    /// <summary>
    /// Transactionally replaces all allocations for a budget plan.
    /// Enforces: max 20 allocations, all allocation currencies must match the plan currency.
    /// </summary>
    public async Task<BudgetPlanDto> ReplaceAllocationsAsync(
        Guid coupleId,
        Guid planId,
        IReadOnlyList<AllocationInput> allocations,
        CancellationToken cancellationToken)
    {
        if (allocations.Count > 20)
            throw new UnprocessableEntityException(
                "BUDGET_ALLOCATION_LIMIT",
                "Um orçamento pode ter no máximo 20 categorias.");

        // Every category goes through the canonical list ("Alimentação" and "alimentacao" are the same).
        allocations = allocations
            .Select(a => a with
            {
                Category = TransactionCategories.TryNormalize(a.Category)
                    ?? throw new BadRequestException("INVALID_CATEGORY", TransactionCategories.InvalidMessage)
            })
            .ToList();

        if (allocations.GroupBy(a => a.Category, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new UnprocessableEntityException(
                "BUDGET_ALLOCATION_DUPLICATE_CATEGORY",
                "Cada categoria só pode aparecer uma vez no orçamento.");

        var plan = await _repository.GetByIdAsync(planId, coupleId, cancellationToken);

        if (plan is null)
            throw new NotFoundException("BUDGET_PLAN_NOT_FOUND", "Orçamento não encontrado.");

        var mismatch = allocations.FirstOrDefault(
            a => !string.Equals(a.Currency, plan.Currency, StringComparison.OrdinalIgnoreCase));

        if (mismatch is not null)
            throw new UnprocessableEntityException(
                "BUDGET_ALLOCATION_CURRENCY_MISMATCH",
                $"A moeda da categoria '{mismatch.Currency}' não corresponde à moeda do orçamento '{plan.Currency}'.");

        var now = _dateTimeProvider.UtcNow;
        var inputs = allocations
            .Select(a => (a.Category, a.AllocatedAmount, a.Currency))
            .ToList();

        try
        {
            var updated = await _repository.ReplaceAllocationsAsync(plan, inputs, now, cancellationToken);
            return MapToDto(updated);
        }
        catch (ConcurrencyConflictException)
        {
            throw new ConflictException(
                "BUDGET_PLAN_CONFLICT",
                "O orçamento foi alterado ao mesmo tempo por outra requisição. Tente novamente.");
        }
    }

    /// <summary>Returns the budget plan for the given couple and month, or null if none exists.</summary>
    public async Task<BudgetPlanDto?> GetPlanAsync(
        Guid coupleId,
        string month,
        CancellationToken cancellationToken)
    {
        var plan = await _repository.GetByMonthAsync(coupleId, month, cancellationToken);
        if (plan is null) return null;

        var (startUtc, endUtc) = ParseMonthWindow(month);
        var actualSpent = await _transactionRepository.GetActualSpentByCategoryAsync(
            coupleId, startUtc, endUtc, cancellationToken);

        return MapToDto(plan, actualSpent);
    }

    /// <summary>Returns the current calendar-month plan for the given couple, or null if none exists.</summary>
    public async Task<BudgetPlanDto?> GetCurrentPlanAsync(
        Guid coupleId,
        CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;
        var currentMonth = BrazilTime.MonthOf(now);
        return await GetPlanAsync(coupleId, currentMonth, cancellationToken);
    }

    /// <summary>
    /// Updates the gross income for the current calendar month.
    /// Auto-creates a plan with zero allocations if none exists (ADR-0011).
    /// </summary>
    public async Task<BudgetPlanDto> UpdateIncomeAsync(
        Guid coupleId,
        decimal grossIncome,
        string currency,
        CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;
        var currentMonth = BrazilTime.MonthOf(now);

        var existing = await _repository.GetByMonthAsync(coupleId, currentMonth, cancellationToken);

        try
        {
            BudgetPlan plan;
            if (existing is null)
            {
                plan = BudgetPlan.Create(coupleId, currentMonth, grossIncome, currency, now);
                await _repository.AddAsync(plan, cancellationToken);
            }
            else
            {
                existing.Update(grossIncome, currency, now);
                plan = existing;
            }

            await _repository.SaveChangesAsync(cancellationToken);
            return MapToDto(plan);
        }
        catch (UniqueViolationException)
        {
            throw new ConflictException(
                "BUDGET_PLAN_DUPLICATE",
                "Já existe um orçamento para este período.");
        }
        catch (ConcurrencyConflictException)
        {
            throw new ConflictException(
                "BUDGET_PLAN_CONFLICT",
                "O orçamento foi alterado ao mesmo tempo por outra requisição. Tente novamente.");
        }
    }

    /// <summary>Computes budget gap = grossIncome − sum of all allocation amounts.</summary>
    public decimal ComputeGap(BudgetPlanDto plan)
        => GapInReais(plan.GrossIncome, plan.Currency, plan.Allocations);

    // Only amounts in reais take part: income of a plan in another currency counts as 0 and allocations in another currency are left out.
    private static decimal GapInReais(decimal grossIncome, string planCurrency, IEnumerable<BudgetAllocationDto> allocations)
        => (CurrencyRules.IsBrl(planCurrency) ? grossIncome : 0m)
           - allocations.Where(a => CurrencyRules.IsBrl(a.Currency)).Sum(a => a.AllocatedAmount);

    private static (DateTime StartUtc, DateTime EndUtc) ParseMonthWindow(string month)
        => BrazilTime.MonthRangeUtc(month);

    private static BudgetPlanDto MapToDto(BudgetPlan plan, Dictionary<string, decimal>? actualSpentMap = null)
    {
        actualSpentMap ??= new();
        var allocations = plan.Allocations
            .Select(a =>
            {
                var spent = actualSpentMap.GetValueOrDefault(TransactionCategories.NormalizeOrOther(a.Category), 0m);
                return new BudgetAllocationDto(a.Id, a.Category, a.AllocatedAmount, a.Currency, spent, a.AllocatedAmount - spent);
            })
            .ToList();
        var gap = GapInReais(plan.GrossIncome, plan.Currency, allocations);
        return new BudgetPlanDto(
            plan.Id,
            plan.CoupleId,
            plan.Month,
            plan.GrossIncome,
            plan.Currency,
            allocations,
            gap,
            plan.CreatedAtUtc,
            plan.UpdatedAtUtc);
    }
}
