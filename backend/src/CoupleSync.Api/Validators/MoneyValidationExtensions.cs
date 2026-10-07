using CoupleSync.Domain.ValueObjects;
using FluentValidation;

namespace CoupleSync.Api.Validators;

/// <summary>
/// The one amount rule shared by every request that carries money: greater than zero, at most
/// <see cref="MoneyRules.MaxAmount"/> and at most two decimal places (never rounded silently).
/// </summary>
public static class MoneyValidationExtensions
{
    public const string NotPositiveMessage = "O valor deve ser maior que zero.";
    public const string TooLargeMessage = "O valor excede o máximo permitido (R$ 999.999.999,99).";
    public const string TooManyDecimalsMessage = "O valor deve ter no máximo duas casas decimais.";

    /// <summary>Returns the failure message for an amount that must be strictly positive, or null when valid.</summary>
    public static string? PositiveAmountError(decimal value)
    {
        if (value <= 0) return NotPositiveMessage;
        return CommonAmountError(value);
    }

    /// <summary>Same as <see cref="PositiveAmountError"/> but zero is allowed (running totals such as a goal's saved amount).</summary>
    public static string? NonNegativeAmountError(decimal value)
    {
        if (value < 0) return "O valor não pode ser negativo.";
        return CommonAmountError(value);
    }

    private static string? CommonAmountError(decimal value)
    {
        if (value > MoneyRules.MaxAmount) return TooLargeMessage;
        if (!MoneyRules.HasAtMostTwoDecimals(value)) return TooManyDecimalsMessage;
        return null;
    }

    public static IRuleBuilderOptionsConditions<T, decimal> PositiveMoney<T>(this IRuleBuilder<T, decimal> rule)
        => rule.Custom((value, ctx) =>
        {
            var error = PositiveAmountError(value);
            if (error is not null) ctx.AddFailure(error);
        });

    public static IRuleBuilderOptionsConditions<T, decimal?> PositiveMoney<T>(this IRuleBuilder<T, decimal?> rule)
        => rule.Custom((value, ctx) =>
        {
            if (value is null) return;
            var error = PositiveAmountError(value.Value);
            if (error is not null) ctx.AddFailure(error);
        });

    public static IRuleBuilderOptionsConditions<T, decimal?> NonNegativeMoney<T>(this IRuleBuilder<T, decimal?> rule)
        => rule.Custom((value, ctx) =>
        {
            if (value is null) return;
            var error = NonNegativeAmountError(value.Value);
            if (error is not null) ctx.AddFailure(error);
        });

    /// <summary>BRL only; an absent currency means BRL.</summary>
    public static IRuleBuilderOptions<T, string?> BrlCurrency<T>(this IRuleBuilder<T, string?> rule)
        => rule.Must(CurrencyRules.IsAccepted).WithMessage(CurrencyRules.InvalidMessage);

    /// <summary>The value must match a canonical category (any case/accents); the message lists the accepted ones.</summary>
    public static IRuleBuilderOptions<T, string?> CanonicalCategory<T>(this IRuleBuilder<T, string?> rule)
        => rule
            .Must(c => !string.IsNullOrWhiteSpace(c)).WithMessage("A categoria é obrigatória.")
            .Must(c => string.IsNullOrWhiteSpace(c) || TransactionCategories.TryNormalize(c) is not null)
            .WithMessage(TransactionCategories.InvalidMessage);
}
