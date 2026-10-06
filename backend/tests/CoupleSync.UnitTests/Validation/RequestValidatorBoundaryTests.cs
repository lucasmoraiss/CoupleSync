using CoupleSync.Api.Contracts.Budget;
using CoupleSync.Api.Contracts.Goals;
using CoupleSync.Api.Contracts.Income;
using CoupleSync.Api.Contracts.Integrations;
using CoupleSync.Api.Contracts.Ocr;
using CoupleSync.Api.Contracts.Transactions;
using CoupleSync.Api.Validators;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Validation;

/// <summary>
/// A07 — payloads that used to reach the database (and fail there with HTTP 500 on PostgreSQL)
/// must be rejected by the request validators.
/// </summary>
public sealed class RequestValidatorBoundaryTests
{
    private const decimal OneE16 = 10_000_000_000_000_000m;
    private const decimal OneE18 = 1_000_000_000_000_000_000m;
    private const decimal OneE20 = 100_000_000_000_000_000_000m;

    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly FixedDateTimeProvider Clock = new(Now);

    // ── POST /transactions ────────────────────────────────────────────────

    private static CreateManualTransactionRequest ManualTransaction(
        decimal amount = 25.90m,
        string? currency = "BRL",
        string? description = "Almoço",
        string? merchant = "Padaria",
        string category = "Alimentação")
        => new(amount, currency, Now.AddDays(-1), description, merchant, category);

    [Fact]
    public void ManualTransaction_Valid_Passes()
    {
        var validator = new CreateManualTransactionRequestValidator();

        Assert.True(validator.Validate(ManualTransaction()).IsValid);
        Assert.True(validator.Validate(ManualTransaction(currency: null, description: null, merchant: null)).IsValid);
        Assert.True(validator.Validate(ManualTransaction(
            amount: MoneyRules.MaxAmount,
            description: new string('d', 512),
            merchant: new string('m', 512),
            category: "OUTROS")).IsValid);
    }

    [Theory]
    [InlineData(513)]
    [InlineData(5000)]
    public void ManualTransaction_DescriptionTooLong_Fails(int length)
        => Assert.False(new CreateManualTransactionRequestValidator()
            .Validate(ManualTransaction(description: new string('d', length))).IsValid);

    [Fact]
    public void ManualTransaction_MerchantTooLong_Fails()
        => Assert.False(new CreateManualTransactionRequestValidator()
            .Validate(ManualTransaction(merchant: new string('m', 5000))).IsValid);

    [Fact]
    public void ManualTransaction_CategoryTooLong_Fails()
        => Assert.False(new CreateManualTransactionRequestValidator()
            .Validate(ManualTransaction(category: new string('c', 65))).IsValid);

    [Theory]
    [InlineData("REAISREAIS")]
    [InlineData("R$")]
    [InlineData("12A")]
    public void ManualTransaction_InvalidCurrency_Fails(string currency)
        => Assert.False(new CreateManualTransactionRequestValidator()
            .Validate(ManualTransaction(currency: currency)).IsValid);

    [Fact]
    public void ManualTransaction_AmountAboveCeiling_Fails()
    {
        var validator = new CreateManualTransactionRequestValidator();

        Assert.False(validator.Validate(ManualTransaction(amount: OneE20)).IsValid);
        Assert.False(validator.Validate(ManualTransaction(amount: MoneyRules.MaxAmount + 0.01m)).IsValid);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.001")]
    public void ManualTransaction_AmountNotPositiveAfterRounding_Fails(string amount)
        => Assert.False(new CreateManualTransactionRequestValidator()
            .Validate(ManualTransaction(amount: decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture))).IsValid);

    // ── POST /integrations/events ─────────────────────────────────────────

    [Fact]
    public void IngestEvent_WithoutCurrency_AssumesBrl()
    {
        var request = new IngestNotificationEventRequest("NUBANK", 10m, null, DateTime.UtcNow.AddMinutes(-5), null, null, null);

        var result = new IngestNotificationEventRequestValidator().Validate(request);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void IngestEvent_EmptyBody_FailsWithoutThrowing()
    {
        var request = new IngestNotificationEventRequest(null!, 0m, null!, default, null, null, null);

        var result = new IngestNotificationEventRequestValidator().Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(IngestNotificationEventRequest.Bank));
    }

    // ── /incomes ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateIncomeSource_BlankName_Fails(string name)
        => Assert.False(new UpdateIncomeSourceRequestValidator()
            .Validate(new UpdateIncomeSourceRequest(name, null, null, null)).IsValid);

    [Fact]
    public void UpdateIncomeSource_NameOmitted_Passes()
        => Assert.True(new UpdateIncomeSourceRequestValidator()
            .Validate(new UpdateIncomeSourceRequest(null, 100m, null, null)).IsValid);

    [Fact]
    public void UpdateIncomeSource_AmountAboveCeiling_Fails()
        => Assert.False(new UpdateIncomeSourceRequestValidator()
            .Validate(new UpdateIncomeSourceRequest(null, OneE20, null, null)).IsValid);

    [Fact]
    public void CreateIncomeSource_AmountAboveCeiling_Fails()
    {
        var validator = new CreateIncomeSourceRequestValidator();

        Assert.False(validator.Validate(new CreateIncomeSourceRequest("2026-10", "Salário", OneE20, "BRL", false, true)).IsValid);
        Assert.True(validator.Validate(new CreateIncomeSourceRequest("2026-10", "Salário", MoneyRules.MaxAmount, "BRL", false, true)).IsValid);
    }

    // ── /budgets ──────────────────────────────────────────────────────────

    [Fact]
    public void UpdateBudgetIncome_AmountAboveCeiling_Fails()
        => Assert.False(new UpdateIncomeRequestValidator().Validate(new UpdateIncomeRequest(OneE20, "BRL")).IsValid);

    [Fact]
    public void CreateBudgetPlan_AmountAboveCeiling_Fails()
        => Assert.False(new CreateBudgetPlanRequestValidator()
            .Validate(new CreateBudgetPlanRequest("2026-10", OneE20, "BRL")).IsValid);

    [Fact]
    public void ReplaceAllocations_AmountAboveCeiling_Fails()
    {
        var validator = new ReplaceAllocationsRequestValidator();

        Assert.False(validator.Validate(new ReplaceAllocationsRequest(
            [new AllocationItemRequest("Moradia", OneE20, "BRL")])).IsValid);
        Assert.True(validator.Validate(new ReplaceAllocationsRequest(
            [new AllocationItemRequest("Moradia", 1500m, "BRL")])).IsValid);
    }

    // ── /goals ────────────────────────────────────────────────────────────

    private static CreateGoalRequest NewGoal(decimal targetAmount)
        => new("Viagem", null, targetAmount, "BRL", Now.AddMonths(6));

    [Theory]
    [InlineData("0.001")]   // stored as 0.00 by numeric(18,2): division by zero on /progress
    [InlineData("0.004")]
    [InlineData("10.005")]  // more than two decimal places
    public void CreateGoal_TargetAmountWithMoreThanTwoDecimals_Fails(string targetAmount)
        => Assert.False(new CreateGoalRequestValidator(Clock)
            .Validate(NewGoal(decimal.Parse(targetAmount, System.Globalization.CultureInfo.InvariantCulture))).IsValid);

    [Fact]
    public void CreateGoal_TargetAmountAboveCeiling_Fails()
    {
        var validator = new CreateGoalRequestValidator(Clock);

        Assert.False(validator.Validate(NewGoal(OneE16)).IsValid);
        Assert.False(validator.Validate(NewGoal(OneE18)).IsValid);
        Assert.True(validator.Validate(NewGoal(MoneyRules.MaxAmount)).IsValid);
        Assert.True(validator.Validate(NewGoal(0.01m)).IsValid);
        Assert.True(validator.Validate(NewGoal(1500.5m)).IsValid);
    }

    [Fact]
    public void UpdateGoal_TargetAmountOutOfBounds_Fails()
    {
        var validator = new UpdateGoalRequestValidator(Clock);

        Assert.False(validator.Validate(new UpdateGoalRequest("Viagem", null, OneE16, null, null)).IsValid);
        Assert.False(validator.Validate(new UpdateGoalRequest("Viagem", null, OneE18, null, null)).IsValid);
        Assert.False(validator.Validate(new UpdateGoalRequest("Viagem", null, 0.001m, null, null)).IsValid);
    }

    [Fact]
    public void UpdateGoal_NegativeCurrentAmount_Fails()
    {
        var validator = new UpdateGoalRequestValidator(Clock);

        Assert.False(validator.Validate(new UpdateGoalRequest("Viagem", null, null, -1m, null)).IsValid);
        Assert.False(validator.Validate(new UpdateGoalRequest(null, null, null, -0.01m, Now.AddMonths(3))).IsValid);
    }

    [Fact]
    public void UpdateGoal_CurrentAmountAboveCeiling_Fails()
        => Assert.False(new UpdateGoalRequestValidator(Clock)
            .Validate(new UpdateGoalRequest("Viagem", null, null, OneE18, null)).IsValid);

    // ── POST /ocr/{uploadId}/confirm ──────────────────────────────────────

    [Fact]
    public void OcrConfirm_Valid_Passes()
        => Assert.True(new ConfirmRequestValidator().Validate(new ConfirmRequest(
            [0, 1],
            [new OcrCategoryOverride(0, "Alimentação"), new OcrCategoryOverride(1, "Transporte")])).IsValid);

    [Fact]
    public void OcrConfirm_RepeatedOverrideIndex_Fails()
        => Assert.False(new ConfirmRequestValidator().Validate(new ConfirmRequest(
            [0, 1],
            [new OcrCategoryOverride(0, "Alimentação"), new OcrCategoryOverride(0, "Transporte")])).IsValid);

    [Theory]
    [InlineData(65)]
    [InlineData(0)]
    public void OcrConfirm_OverrideCategoryWithInvalidLength_Fails(int length)
        => Assert.False(new ConfirmRequestValidator().Validate(new ConfirmRequest(
            [0],
            [new OcrCategoryOverride(0, new string('c', length))])).IsValid);
}
