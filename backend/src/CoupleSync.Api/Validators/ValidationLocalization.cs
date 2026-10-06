using System.Globalization;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Validators;

/// <summary>
/// Makes the built-in FluentValidation and model-binding messages come out in Brazilian Portuguese
/// ("'E-mail' não pode ficar vazio" instead of "'Email' must not be empty").
/// Custom rules still pass their own <c>WithMessage</c> text.
/// </summary>
public static class ValidationLocalization
{
    private static readonly IReadOnlyDictionary<string, string> FieldLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["AllocatedAmount"] = "Valor alocado",
        ["Allocations"] = "Categorias",
        ["Amount"] = "Valor",
        ["Bank"] = "Banco",
        ["CandidateEdits"] = "Transações editadas",
        ["Category"] = "Categoria",
        ["CategoryOverrides"] = "Categorias alteradas",
        ["Content"] = "Mensagem",
        ["Currency"] = "Moeda",
        ["CurrentAmount"] = "Valor atual",
        ["ManualAmount"] = "Valor guardado",
        ["Deadline"] = "Prazo",
        ["Description"] = "Descrição",
        ["Email"] = "E-mail",
        ["EventTimestamp"] = "Data do evento",
        ["GrossIncome"] = "Renda bruta",
        ["History"] = "Histórico",
        ["JoinCode"] = "Código de convite",
        ["Merchant"] = "Estabelecimento",
        ["Message"] = "Mensagem",
        ["Month"] = "Mês",
        ["Name"] = "Nome",
        ["Password"] = "Senha",
        ["RawNotificationText"] = "Texto da notificação",
        ["RefreshToken"] = "Token de renovação",
        ["Role"] = "Autor",
        ["TargetAmount"] = "Valor da meta",
        ["Title"] = "Título"
    };

    /// <summary>Portuguese label of a request property (falls back to the property name).</summary>
    public static string LabelFor(string propertyName)
        => FieldLabels.TryGetValue(propertyName, out var label) ? label : propertyName;

    public static void Configure()
    {
        ValidatorOptions.Global.LanguageManager.Culture = new CultureInfo("pt-BR");
        ValidatorOptions.Global.DisplayNameResolver = (_, member, _) =>
            member is null ? null : LabelFor(member.Name);
    }

    /// <summary>Messages MVC generates when binding the request (wrong type, missing body...).</summary>
    public static void ConfigureModelBinding(MvcOptions options)
    {
        var messages = options.ModelBindingMessageProvider;
        messages.SetValueMustNotBeNullAccessor(_ => "Este campo é obrigatório.");
        messages.SetMissingBindRequiredValueAccessor(name => $"O campo '{name}' é obrigatório.");
        messages.SetMissingKeyOrValueAccessor(() => "É necessário informar um valor.");
        messages.SetMissingRequestBodyRequiredValueAccessor(() => "O corpo da requisição é obrigatório.");
        messages.SetValueIsInvalidAccessor(_ => "Valor inválido.");
        messages.SetAttemptedValueIsInvalidAccessor((_, name) => $"Valor inválido para '{name}'.");
        messages.SetUnknownValueIsInvalidAccessor(name => $"Valor inválido para '{name}'.");
        messages.SetNonPropertyAttemptedValueIsInvalidAccessor(_ => "Valor inválido.");
        messages.SetNonPropertyUnknownValueIsInvalidAccessor(() => "Valor inválido.");
        messages.SetNonPropertyValueMustBeANumberAccessor(() => "Informe um número válido.");
        messages.SetValueMustBeANumberAccessor(name => $"O campo '{name}' deve ser um número válido.");
    }
}
