---
description: "Use when reviewing or modifying financial data handling — transactions, budgets, goals, cash flow projections, or any monetary calculations in CoupleSync."
---
# Financial Domain Rules (CoupleSync)

## Couple-Level Data Isolation (CRITICAL)
- Every query MUST filter by `CoupleId` — this is a security invariant
- Never expose data from one couple to another
- Test data isolation in integration tests

## Transaction Handling
- Transactions come from Android notification listener (no retroactive history)
- Support manual transaction entry as fallback
- Split transactions: one parent → multiple category allocations
- Re-categorization: user can override auto-detected categories

## Budget & Goals
- BudgetPlan has BudgetAllocations per category
- Goals are couple-scoped with TargetAmount, CurrentAmount, Deadline
- Cash flow projections based on recurring transactions

## Financial UX Language
- Use neutral, non-judgmental language ("spent" not "wasted")
- Show progress toward goals positively
- Amounts always formatted with currency symbol and 2 decimal places
- Date formats: user locale-aware
