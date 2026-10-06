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
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("O banco é obrigatório.")
            .MaximumLength(64).WithMessage("O nome do banco deve ter no máximo 64 caracteres.")
            .Must(b => AllowedBanks.Contains(b.Trim().ToUpperInvariant()))
            .WithMessage("O banco '{PropertyValue}' não é suportado. Bancos suportados: " + string.Join(", ", AllowedBanks));

        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("O valor deve ser maior que zero.")
            .LessThanOrEqualTo(1_000_000).WithMessage("O valor excede o máximo permitido.");

        RuleFor(x => x.Currency)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("A moeda é obrigatória.")
            .Must(c => AllowedCurrencies.Contains(c.Trim().ToUpperInvariant()))
            .WithMessage("A moeda '{PropertyValue}' não é suportada. Use BRL, USD ou EUR.");

        RuleFor(x => x.EventTimestamp)
            .NotEmpty().WithMessage("A data do evento é obrigatória.")
            .Must(ts => ts <= DateTime.UtcNow.AddMinutes(5))
            .WithMessage("A data do evento não pode estar no futuro.");

        RuleFor(x => x.Description)
            .MaximumLength(512).When(x => x.Description != null)
            .WithMessage("A descrição deve ter no máximo 512 caracteres.");

        RuleFor(x => x.Merchant)
            .MaximumLength(512).When(x => x.Merchant != null)
            .WithMessage("O estabelecimento deve ter no máximo 512 caracteres.");

        RuleFor(x => x.RawNotificationText)
            .MaximumLength(2048).When(x => x.RawNotificationText != null)
            .WithMessage("O texto da notificação deve ter no máximo 2048 caracteres.");
    }
}
