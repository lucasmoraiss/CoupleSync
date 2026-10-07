using System.Text.RegularExpressions;
using CoupleSync.Api.Validators;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CoupleSync.Api.Errors;

/// <summary>
/// Turns a failed model binding / FluentValidation run (automatic 400 of <c>[ApiController]</c>) into the
/// standard error format. Wired through <c>ApiBehaviorOptions.InvalidModelStateResponseFactory</c>.
/// </summary>
public static partial class InvalidModelStateResponse
{
    public const string InvalidBodyMessage = "Os dados enviados estão em um formato inválido.";
    private const string DefaultMessage = "Dados inválidos. Verifique os campos e tente novamente.";

    public static IActionResult Create(ActionContext context)
    {
        var bodyParameters = context.ActionDescriptor.Parameters
            .Where(p => p.BindingInfo?.BindingSource == BindingSource.Body)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var fields = new Dictionary<string, List<string>>();
        var general = new List<string>();
        var malformedBody = false;

        foreach (var (rawKey, entry) in context.ModelState)
        {
            if (entry.ValidationState != ModelValidationState.Invalid) continue;

            // System.Text.Json reports its failures under "$" / "$.path" with an English, technical text
            // (position, CLR type names). The client only needs to know the body could not be read.
            if (rawKey.StartsWith('$'))
            {
                malformedBody = true;
                continue;
            }

            // The [FromBody] parameter itself ("request") is not a field the user can fill in.
            var key = bodyParameters.Contains(rawKey) ? string.Empty : rawKey;

            foreach (var error in entry.Errors)
            {
                var message = Translate(error.ErrorMessage);
                if (message is null) continue;

                if (key.Length == 0) general.Add(message);
                else AddField(fields, key, message);
            }
        }

        if (malformedBody)
        {
            return ApiErrors.Result(context.HttpContext, StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidRequestBody, InvalidBodyMessage);
        }

        IReadOnlyDictionary<string, string[]>? errors = fields.Count == 0
            ? null
            : fields.ToDictionary(f => f.Key, f => f.Value.Distinct().ToArray());

        var summary = general.FirstOrDefault() ?? fields.Values.FirstOrDefault()?.FirstOrDefault() ?? DefaultMessage;
        return ApiErrors.Result(context.HttpContext, StatusCodes.Status400BadRequest,
            ApiErrorCodes.ValidationError, summary, errors);
    }

    /// <summary>
    /// FluentValidation and the custom binding messages are already Portuguese. What is left is the
    /// DataAnnotations implicit "required" (non-nullable property missing from the JSON).
    /// </summary>
    private static string? Translate(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var required = ImplicitRequiredPattern().Match(message);
        return required.Success
            ? $"O campo '{ValidationLocalization.LabelFor(required.Groups[1].Value)}' é obrigatório."
            : message;
    }

    private static void AddField(Dictionary<string, List<string>> fields, string key, string message)
    {
        if (!fields.TryGetValue(key, out var list))
            fields[key] = list = new List<string>();
        list.Add(message);
    }

    [GeneratedRegex(@"^The (\w+) field is required\.$")]
    private static partial Regex ImplicitRequiredPattern();
}
