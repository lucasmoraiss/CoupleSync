using CoupleSync.Api.Contracts.Integrations;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class IngestNotificationEventRequestValidator : AbstractValidator<IngestNotificationEventRequest>
{
    private static readonly string[] AllowedCurrencies = ["BRL", "USD", "EUR"];
    private static readonly string[] AllowedBanks = ["NUBANK", "ITAU", "INTER", "C6", "BRADESCO", "XP", "BTG", "SANTANDER", "CAIXA", "BB"];

    public IngestNotificationEventRequestValidator()
    {
        RuleFor(x => x.Bank)
            .NotEmpty().WithMessage("Bank is required.")
            .MaximumLength(64).WithMessage("Bank name must not exceed 64 characters.")
            .Must(b => AllowedBanks.Contains(b.Trim().ToUpperInvariant()))
            .WithMessage("Bank '{PropertyValue}' is not supported. Supported banks: " + string.Join(", ", AllowedBanks));

        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("Amount must be greater than zero.")
            .LessThanOrEqualTo(1_000_000).WithMessage("Amount exceeds maximum allowed value.");

        RuleFor(x => x.Currency)
            .NotEmpty().WithMessage("Currency is required.")
            .Must(c => AllowedCurrencies.Contains(c.Trim().ToUpperInvariant()))
            .WithMessage("Currency '{PropertyValue}' is not supported. Supported: BRL, USD, EUR.");

        RuleFor(x => x.EventTimestamp)
            .NotEmpty().WithMessage("EventTimestamp is required.")
            .Must(ts => ts <= DateTime.UtcNow.AddMinutes(5))
            .WithMessage("EventTimestamp cannot be in the future.");

        RuleFor(x => x.Description)
            .MaximumLength(512).When(x => x.Description != null)
            .WithMessage("Description must not exceed 512 characters.");

        RuleFor(x => x.Merchant)
            .MaximumLength(512).When(x => x.Merchant != null)
            .WithMessage("Merchant must not exceed 512 characters.");

        RuleFor(x => x.RawNotificationText)
            .MaximumLength(2048).When(x => x.RawNotificationText != null)
            .WithMessage("RawNotificationText must not exceed 2048 characters (will be truncated at storage).");
    }
}
