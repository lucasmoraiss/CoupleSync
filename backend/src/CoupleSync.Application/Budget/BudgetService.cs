using CoupleSync.Application.Budget.Commands;
using CoupleSync.Application.Budget.Queries;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

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
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(
                "BUDGET_PLAN_CONFLICT",
                "Budget plan was modified concurrently. Please retry the request.");
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("23505") == true
                                           || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new ConflictException(
                "BUDGET_PLAN_CONFLICT",
                "A budget plan for this month already exists. Please retry the request.");
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
                "A budget plan may have at most 20 allocations.");

        if (allocations.GroupBy(a => a.Category, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new UnprocessableEntityException(
                "BUDGET_ALLOCATION_DUPLICATE_CATEGORY",
                "Each category may appear at most once in a budget plan.");

        var plan = await _repository.GetByIdAsync(planId, coupleId, cancellationToken);

        if (plan is null)
            throw new NotFoundException("BUDGET_PLAN_NOT_FOUND", "Budget plan not found.");

        var mismatch = allocations.FirstOrDefault(
            a => !string.Equals(a.Currency, plan.Currency, StringComparison.OrdinalIgnoreCase));

        if (mismatch is not null)
            throw new UnprocessableEntityException(
                "BUDGET_ALLOCATION_CURRENCY_MISMATCH",
                $"Allocation currency '{mismatch.Currency}' does not match plan currency '{plan.Currency}'.");

        var now = _dateTimeProvider.UtcNow;
        var inputs = allocations
            .Select(a => (a.Category, a.AllocatedAmount, a.Currency))
            .ToList();

        try
        {
            var updated = await _repository.ReplaceAllocationsAsync(plan, inputs, now, cancellationToken);
            return MapToDto(updated);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(
                "BUDGET_PLAN_CONFLICT",
                "Budget plan was modified concurrently. Please retry the request.");
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
        var currentMonth = $"{now.Year:D4}-{now.Month:D2}";
        return await GetPlanAsync(coupleId, currentMonth, cancellationToken);
    }

    /// <summary>
    /// Updates the gross income for the current calendar month.
    /// Auto-creates a plan with zero allocations if none exists (ADR-005).
    /// </summary>
    public async Task<BudgetPlanDto> UpdateIncomeAsync(
        Guid coupleId,
        decimal grossIncome,
        string currency,
        CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;
        var currentMonth = $"{now.Year:D4}-{now.Month:D2}";

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
        catch (DbUpdateException ex)
            when (ex.InnerException?.Message.Contains("23505") == true
               || ex.InnerException?.Message.Contains("unique constraint") == true)
        {
            throw new ConflictException(
                "BUDGET_PLAN_DUPLICATE",
                "A budget plan for this period already exists.");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(
                "BUDGET_PLAN_CONFLICT",
                "Budget plan was modified concurrently. Please retry the request.");
        }
    }

    /// <summary>Computes budget gap = grossIncome − sum of all allocation amounts.</summary>
    public decimal ComputeGap(BudgetPlanDto plan)
        => plan.GrossIncome - plan.Allocations.Sum(a => a.AllocatedAmount);

    private static (DateTime StartUtc, DateTime EndUtc) ParseMonthWindow(string month)
    {
        var parts = month.Split('-');
        var year = int.Parse(parts[0]);
        var monthNum = int.Parse(parts[1]);
        var start = new DateTime(year, monthNum, 1, 0, 0, 0, DateTimeKind.Utc);
        return (start, start.AddMonths(1));
    }

    private static BudgetPlanDto MapToDto(BudgetPlan plan, Dictionary<string, decimal>? actualSpentMap = null)
    {
        actualSpentMap ??= new();
        var allocations = plan.Allocations
            .Select(a =>
            {
                var spent = actualSpentMap.GetValueOrDefault(a.Category, 0m);
                return new BudgetAllocationDto(a.Id, a.Category, a.AllocatedAmount, a.Currency, spent, a.AllocatedAmount - spent);
            })
            .ToList();
        var gap = plan.GrossIncome - allocations.Sum(a => a.AllocatedAmount);
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
