using CoupleSync.Application.Auth;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public static class PasswordPolicyValidationExtensions
{
    /// <summary>
    /// Applies <see cref="PasswordPolicy"/> to a new password, one failure per missing requirement.
    /// <paramref name="email"/> (when the request carries it) rejects a password equal to the e-mail.
    /// </summary>
    public static IRuleBuilderOptionsConditions<T, string> MeetsPasswordPolicy<T>(
        this IRuleBuilder<T, string> ruleBuilder,
        Func<T, string?>? email = null)
    {
        return ruleBuilder.Custom((password, context) =>
        {
            foreach (var problem in PasswordPolicy.Validate(password, email?.Invoke(context.InstanceToValidate)))
            {
                context.AddFailure(problem);
            }
        });
    }
}
