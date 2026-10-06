using CoupleSync.Api.Contracts.Budget;
using CoupleSync.Api.Contracts.Goals;
using CoupleSync.Api.Contracts.Income;
using CoupleSync.Api.Contracts.Integrations;
using CoupleSync.Api.Contracts.Ocr;
using CoupleSync.Api.Contracts.Transactions;
using CoupleSync.Api.Validators;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.UnitTests.Support;
using FluentValidation.Results;

namespace CoupleSync.UnitTests.Validation;

/// <summary>
/// One amount rule (greater than zero, at most 999,999,999.99, at most two decimals, never rounded
/// silently) and one currency rule (BRL only, absent means BRL) in every request that carries money.
/// </summary>
public sealed class MoneyAndCurrencyValidationTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly FixedDateTimeProvider Clock = new(Now);

    private static readonly Func<decimal, ValidationResult>[] AmountValidators =
    [
        a => new CreateManualTransactionRequestValidator().Validate(new CreateManualTransactionRequest(a, "BRL", Now, "d", "m", "LAZER")),
        a => new IngestNotificationEventRequestValidator().Validate(new IngestNotificationEventRequest("NUBANK", a, "BRL", Now.AddMinutes(-10), null, null, null)),
        a => new CreateGoalRequestValidator(Clock).Validate(new CreateGoalRequest("Viagem", null, a, "BRL", Now.AddDays(30))),
        a => new UpdateGoalRequestValidator(Clock).Validate(new UpdateGoalRequest(null, null, a, null, null)),
        a => new CreateIncomeSourceRequestValidator().Validate(new CreateIncomeSourceRequest("2026-10", "Salário", a, "BRL", false, true)),
        a => new UpdateIncomeSourceRequestValidator().Validate(new UpdateIncomeSourceRequest(null, a, null, null)),
        a => new CreateBudgetPlanRequestValidator().Validate(new CreateBudgetPlanRequest("2026-10", a, "BRL")),
        a => new UpdateIncomeRequestValidator().Validate(new UpdateIncomeRequest(a, "BRL")),
        a => new ReplaceAllocationsRequestValidator().Validate(new ReplaceAllocationsRequest([new AllocationItemRequest("LAZER", a, "BRL")])),
        a => new ConfirmRequestValidator().Validate(new ConfirmRequest([0], null, [new OcrCandidateEdit(0, null, a)])),
    ];

    public static IEnumerable<object[]> ValidatorIndexes() => Enumerable.Range(0, AmountValidators.Length).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(ValidatorIndexes))]
    public void ValidAmounts_Pass(int validator)
    {
        foreach (var amount in new[] { 0.01m, 1m, 10.5m, 10.50m, 1234.56m, 999_999_999.99m })
            Assert.True(AmountValidators[validator](amount).IsValid, $"validator {validator}, amount {amount}");
    }

    [Theory]
    [MemberData(nameof(ValidatorIndexes))]
    public void ZeroAndNegative_AreRejected(int validator)
    {
        foreach (var amount in new[] { 0m, -0.01m, -1m, -999_999_999.99m })
            Assert.False(AmountValidators[validator](amount).IsValid, $"validator {validator}, amount {amount}");
    }

    [Theory]
    [MemberData(nameof(ValidatorIndexes))]
    public void AboveTheCeiling_IsRejected(int validator)
    {
        foreach (var amount in new[] { 1_000_000_000m, 999_999_999.991m, 1_000_000_000_000m, 100_000_000_000_000_000_000m })
            Assert.False(AmountValidators[validator](amount).IsValid, $"validator {validator}, amount {amount}");
    }

    [Theory]
    [MemberData(nameof(ValidatorIndexes))]
    public void ThreeDecimals_AreRejectedInsteadOfRounded(int validator)
    {
        foreach (var amount in new[] { 10.001m, 10.005m, 10.999m, 0.001m, 1.234m })
        {
            var result = AmountValidators[validator](amount);
            Assert.False(result.IsValid, $"validator {validator}, amount {amount}");
        }
    }

    [Fact]
    public void Messages_AreInPortuguese()
    {
        Assert.Equal("O valor deve ser maior que zero.", MoneyValidationExtensions.PositiveAmountError(0m));
        Assert.Contains("máximo", MoneyValidationExtensions.PositiveAmountError(1_000_000_000m));
        Assert.Contains("duas casas decimais", MoneyValidationExtensions.PositiveAmountError(1.234m));
        Assert.Null(MoneyValidationExtensions.PositiveAmountError(10.55m));
    }

    [Fact]
    public void MoneyRules_CeilingIsNineIntegerDigits()
        => Assert.Equal(999_999_999.99m, MoneyRules.MaxAmount);

    [Fact]
    public void GoalCurrentAmount_AllowsZero_ButNotNegativeOrThreeDecimals()
    {
        var validator = new UpdateGoalRequestValidator(Clock);

        Assert.True(validator.Validate(new UpdateGoalRequest(null, null, null, 0m, null)).IsValid);
        Assert.True(validator.Validate(new UpdateGoalRequest(null, null, null, 250.40m, null)).IsValid);
        Assert.False(validator.Validate(new UpdateGoalRequest(null, null, null, -1m, null)).IsValid);
        Assert.False(validator.Validate(new UpdateGoalRequest(null, null, null, 10.001m, null)).IsValid);
        Assert.False(validator.Validate(new UpdateGoalRequest(null, null, null, 1_000_000_000m, null)).IsValid);
    }

    // ── currency ─────────────────────────────────────────────────────────

    private static readonly Func<string?, ValidationResult>[] CurrencyValidators =
    [
        c => new CreateManualTransactionRequestValidator().Validate(new CreateManualTransactionRequest(10m, c, Now, "d", "m", "LAZER")),
        c => new IngestNotificationEventRequestValidator().Validate(new IngestNotificationEventRequest("NUBANK", 10m, c, Now.AddMinutes(-10), null, null, null)),
        c => new CreateGoalRequestValidator(Clock).Validate(new CreateGoalRequest("Viagem", null, 10m, c, Now.AddDays(30))),
        c => new CreateIncomeSourceRequestValidator().Validate(new CreateIncomeSourceRequest("2026-10", "Salário", 10m, c, false, true)),
        c => new CreateBudgetPlanRequestValidator().Validate(new CreateBudgetPlanRequest("2026-10", 10m, c)),
        c => new UpdateIncomeRequestValidator().Validate(new UpdateIncomeRequest(10m, c)),
        c => new ReplaceAllocationsRequestValidator().Validate(new ReplaceAllocationsRequest([new AllocationItemRequest("LAZER", 10m, c)])),
    ];

    public static IEnumerable<object[]> CurrencyValidatorIndexes() => Enumerable.Range(0, CurrencyValidators.Length).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(CurrencyValidatorIndexes))]
    public void AbsentOrBrl_IsAccepted(int validator)
    {
        foreach (var currency in new[] { null, "", "BRL", "brl", " BRL " })
            Assert.True(CurrencyValidators[validator](currency).IsValid, $"validator {validator}, currency '{currency}'");
    }

    [Theory]
    [MemberData(nameof(CurrencyValidatorIndexes))]
    public void AnyOtherCurrency_IsRejectedInPortuguese(int validator)
    {
        foreach (var currency in new[] { "USD", "EUR", "usd", "R$", "REAIS" })
        {
            var result = CurrencyValidators[validator](currency);
            Assert.False(result.IsValid, $"validator {validator}, currency '{currency}'");
            Assert.Contains(result.Errors, e => e.ErrorMessage == "Só é aceita a moeda BRL (real).");
        }
    }

    // ── category ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ALIMENTACAO", true)]
    [InlineData("Alimentação", true)]
    [InlineData("alimentacao ", true)]
    [InlineData("saúde", true)]
    [InlineData("OUTROS", true)]
    [InlineData("Mercado", false)]
    [InlineData("Educação", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void EveryCategoryEntryPoint_UsesTheCanonicalList(string category, bool valid)
    {
        Assert.Equal(valid, new CreateManualTransactionRequestValidator()
            .Validate(new CreateManualTransactionRequest(10m, "BRL", Now, "d", "m", category)).IsValid);
        Assert.Equal(valid, new PatchTransactionCategoryRequestValidator()
            .Validate(new PatchTransactionCategoryRequest(category)).IsValid);
        Assert.Equal(valid, new ReplaceAllocationsRequestValidator()
            .Validate(new ReplaceAllocationsRequest([new AllocationItemRequest(category, 10m, "BRL")])).IsValid);
        Assert.Equal(valid, new ConfirmRequestValidator()
            .Validate(new ConfirmRequest([0], [new OcrCategoryOverride(0, category)], null)).IsValid);
    }

    [Fact]
    public void RejectedCategory_ReportsTheAcceptedList()
    {
        var result = new PatchTransactionCategoryRequestValidator().Validate(new PatchTransactionCategoryRequest("Mercado"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(TransactionCategories.InvalidMessage, error.ErrorMessage);
        Assert.Contains("ALIMENTACAO", error.ErrorMessage);
        Assert.Contains("OUTROS", error.ErrorMessage);
    }
}
