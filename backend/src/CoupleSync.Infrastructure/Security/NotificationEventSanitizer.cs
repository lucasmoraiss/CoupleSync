using System.Text.RegularExpressions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Infrastructure.Security;

public sealed class NotificationEventSanitizer : INotificationEventSanitizer
{
    // Removes HTML tags and common script injection patterns
    private static readonly Regex HtmlTagsRegex = new(@"<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex ScriptContentRegex = new(@"(javascript:|vbscript:|data:)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string SanitizeText(string? input, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var sanitized = input.Trim();
        sanitized = HtmlTagsRegex.Replace(sanitized, string.Empty);
        sanitized = ScriptContentRegex.Replace(sanitized, string.Empty);
        sanitized = sanitized.Trim();

        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized[..maxLength];
        }

        return sanitized;
    }
}
