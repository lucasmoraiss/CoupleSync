namespace CoupleSync.Application.Common.Interfaces;

public interface INotificationEventSanitizer
{
    string SanitizeText(string? input, int maxLength);
}
