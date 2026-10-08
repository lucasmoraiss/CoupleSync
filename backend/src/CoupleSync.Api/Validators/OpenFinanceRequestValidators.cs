using CoupleSync.Api.Contracts.OpenFinance;
using CoupleSync.Domain.Entities;
using FluentValidation;

namespace CoupleSync.Api.Validators;

internal static class OpenFinanceValidationRules
{
    public const int MaxCredentialLength = 200;
}

public sealed class TestCredentialsRequestValidator : AbstractValidator<TestCredentialsRequest>
{
    public TestCredentialsRequestValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty().MaximumLength(OpenFinanceValidationRules.MaxCredentialLength);
        RuleFor(x => x.ClientSecret).NotEmpty().MaximumLength(OpenFinanceValidationRules.MaxCredentialLength);
    }
}

public sealed class CreateBankConnectionRequestValidator : AbstractValidator<CreateBankConnectionRequest>
{
    public CreateBankConnectionRequestValidator()
    {
        RuleFor(x => x.Label).NotEmpty().MaximumLength(BankConnection.MaxLabelLength);
        RuleFor(x => x.ClientId).NotEmpty().MaximumLength(OpenFinanceValidationRules.MaxCredentialLength);
        RuleFor(x => x.ClientSecret).NotEmpty().MaximumLength(OpenFinanceValidationRules.MaxCredentialLength);
        RuleFor(x => x.HistoryMonths)
            .Must(months => months is null || BankConnection.AllowedHistoryMonths.Contains(months.Value))
            .WithMessage("O período deve ser de 3, 6 ou 12 meses.");
    }
}

public sealed class AddBankItemRequestValidator : AbstractValidator<AddBankItemRequest>
{
    public AddBankItemRequestValidator()
    {
        // The id goes into the address of the call to Pluggy: only what an identifier can contain.
        RuleFor(x => x.ItemId)
            .NotEmpty()
            .MaximumLength(BankItem.MaxPluggyIdLength)
            .Matches(@"^\s*[A-Za-z0-9-]+\s*$")
            .WithMessage("O Item ID deve ter apenas letras, números e hífens. Copie de novo pelo menu de três pontos da conexão.");
    }
}

public sealed class UpdateBankAccountRequestValidator : AbstractValidator<UpdateBankAccountRequest>
{
    public UpdateBankAccountRequestValidator()
    {
        RuleFor(x => x.SyncEnabled).NotNull();
    }
}
