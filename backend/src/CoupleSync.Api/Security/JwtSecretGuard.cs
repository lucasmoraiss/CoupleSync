namespace CoupleSync.Api.Security;

/// <summary>
/// Validates the JWT signing secret at startup. The application must refuse to boot
/// when the secret is missing, too short or still the documentation placeholder.
/// </summary>
public static class JwtSecretGuard
{
    public const string Placeholder = "REPLACE_WITH_ENV_JWT_SECRET_32CHARS_MIN";
    public const int MinimumLength = 32;

    public static bool IsValid(string? secret)
    {
        return !string.IsNullOrWhiteSpace(secret)
            && secret.Length >= MinimumLength
            && !string.Equals(secret, Placeholder, StringComparison.Ordinal);
    }

    public static void EnsureValid(string? secret)
    {
        if (!IsValid(secret))
        {
            throw new InvalidOperationException(
                "Invalid JWT secret configuration. Configure Jwt:Secret (or JWT__SECRET) with a non-placeholder value and at least 32 characters.");
        }
    }
}
