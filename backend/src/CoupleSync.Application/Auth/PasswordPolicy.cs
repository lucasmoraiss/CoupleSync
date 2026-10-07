namespace CoupleSync.Application.Auth;

/// <summary>
/// The single rule for a NEW password (registration, password change, and the future password reset).
/// Existing passwords are never re-checked, so accounts created under the old rule keep logging in.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 8;

    public const string TooShortMessage = "A senha precisa ter pelo menos 8 caracteres.";
    public const string NeedsLetterMessage = "A senha precisa ter pelo menos uma letra.";
    public const string NeedsDigitMessage = "A senha precisa ter pelo menos um número.";
    public const string TooCommonMessage = "Essa senha é muito comum. Escolha outra.";
    public const string SameAsEmailMessage = "A senha não pode ser igual ao seu e-mail.";

    // Compared case-insensitively. Short on purpose: a list of obvious choices, not a breach database.
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "12345678", "123456789", "1234567890", "87654321", "11111111", "00000000", "12341234", "11223344",
        "password", "password1", "password12", "password123", "passw0rd", "p@ssw0rd", "p@ssword",
        "senha123", "senha1234", "senha12345", "senha@123", "senhasenha", "mudar123", "mudar@123",
        "qwerty123", "qwertyui", "qwertyuiop", "abc12345", "abcd1234", "abc123456", "iloveyou", "admin123",
        "letmein1", "welcome1", "welcome123", "couplesync", "couplesync1", "couplesync123"
    };

    /// <summary>Messages (pt-BR) describing exactly what is missing; empty when the password is acceptable.</summary>
    public static IReadOnlyList<string> Validate(string? password, string? email = null)
    {
        var problems = new List<string>();
        var value = password ?? string.Empty;

        if (value.Length < MinimumLength)
        {
            problems.Add(TooShortMessage);
        }

        if (!value.Any(char.IsLetter))
        {
            problems.Add(NeedsLetterMessage);
        }

        if (!value.Any(char.IsAsciiDigit))
        {
            problems.Add(NeedsDigitMessage);
        }

        if (value.Length >= MinimumLength && IsObvious(value, email))
        {
            problems.Add(CommonPasswords.Contains(value.Trim()) ? TooCommonMessage : SameAsEmailMessage);
        }

        return problems;
    }

    private static bool IsObvious(string password, string? email)
    {
        var candidate = password.Trim();

        if (CommonPasswords.Contains(candidate))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var normalizedEmail = email.Trim();
        var localPart = normalizedEmail.Split('@')[0];

        return string.Equals(candidate, normalizedEmail, StringComparison.OrdinalIgnoreCase)
            || (localPart.Length >= MinimumLength && string.Equals(candidate, localPart, StringComparison.OrdinalIgnoreCase));
    }
}
