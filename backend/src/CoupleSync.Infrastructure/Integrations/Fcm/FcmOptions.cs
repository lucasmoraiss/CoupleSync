namespace CoupleSync.Infrastructure.Integrations.Fcm;

/// <summary>
/// Firebase Cloud Messaging configuration. Values must come from environment variables or
/// secrets management — never hardcoded in source.
/// </summary>
public sealed record FcmOptions
{
    /// <summary>Firebase project ID (e.g. "my-project-12345").</summary>
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>
    /// Full Firebase service account JSON as a string.
    /// SECURITY: this value must never be logged.
    /// </summary>
    public string CredentialJson { get; init; } = string.Empty;
}
