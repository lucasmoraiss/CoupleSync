using CoupleSync.Api.Contracts.Auth;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.RefreshToken)
            .NotEmpty();
    }
}
