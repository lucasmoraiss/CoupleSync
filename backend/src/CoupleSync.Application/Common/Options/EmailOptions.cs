namespace CoupleSync.Application.Common.Options;

/// <summary>Bound to the <c>Email</c> section (env: <c>Email__Provider</c>, <c>Email__ApiKey</c>, <c>Email__FromAddress</c>, <c>Email__FromName</c>).</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public const string BrevoProvider = "brevo";

    /// <summary><c>brevo</c> or empty (sending off).</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Secret. Never logged.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "CoupleSync";

    public bool IsConfigured =>
        string.Equals(Provider?.Trim(), BrevoProvider, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(FromAddress);
}
