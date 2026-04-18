using System.Net;
using CoupleSync.Application.Common.Interfaces;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoupleSync.Infrastructure.Integrations.Fcm;

public sealed class FcmAdapter : IFcmAdapter
{
    private readonly FcmOptions _options;
    private readonly ILogger<FcmAdapter> _logger;
    private readonly object _initLock = new();
    private volatile FirebaseApp? _app;

    public FcmAdapter(IOptions<FcmOptions> options, ILogger<FcmAdapter> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> SendAsync(string deviceToken, string title, string body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.ProjectId) || string.IsNullOrEmpty(_options.CredentialJson))
        {
            _logger.LogWarning("FCM is not configured (Fcm:ProjectId or Fcm:CredentialJson is empty). Skipping push dispatch.");
            return false;
        }

        EnsureFirebaseInitialized();

        var message = new Message
        {
            Token = deviceToken,
            Notification = new Notification
            {
                Title = title,
                Body = body
            }
        };

        try
        {
            await FirebaseMessaging.DefaultInstance.SendAsync(message, ct);
            return true;
        }
        catch (FirebaseMessagingException ex) when (
            ex.MessagingErrorCode == MessagingErrorCode.Unregistered ||
            ex.MessagingErrorCode == MessagingErrorCode.InvalidArgument)
        {
            _logger.LogWarning(
                "FCM permanent failure for token (MessagingErrorCode={ErrorCode}). Marking event as failed.",
                ex.MessagingErrorCode);
            return false;
        }
        catch (FirebaseMessagingException ex) when (
            ex.HttpResponse?.StatusCode == HttpStatusCode.NotFound ||
            ex.HttpResponse?.StatusCode == HttpStatusCode.BadRequest)
        {
            _logger.LogWarning(
                "FCM permanent HTTP failure (StatusCode={StatusCode}). Marking event as failed.",
                ex.HttpResponse?.StatusCode);
            return false;
        }
    }

    private void EnsureFirebaseInitialized()
    {
        if (_app is not null)
            return;

        lock (_initLock)
        {
            if (_app is not null)
                return;

            var credential = GoogleCredential.FromJson(_options.CredentialJson)
                .CreateScoped("https://www.googleapis.com/auth/firebase.messaging");

            _app = FirebaseApp.Create(new AppOptions
            {
                Credential = credential,
                ProjectId = _options.ProjectId
            });
        }
    }
}
