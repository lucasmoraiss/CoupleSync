using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using Microsoft.Extensions.Options;

namespace CoupleSync.Infrastructure.Integrations.Email;

public sealed class EmailSendException : Exception
{
    public EmailSendException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// Brevo transactional e-mail over HTTPS (Render blocks the SMTP ports). The API key goes only in the
/// <c>api-key</c> header and is never logged or put in an exception message; neither is the response body,
/// which can echo the recipient.
/// </summary>
public sealed class BrevoEmailClient
{
    public const string Endpoint = "https://api.brevo.com/v3/smtp/email";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _httpClient;
    private readonly EmailOptions _options;

    public BrevoEmailClient(HttpClient httpClient, IOptions<EmailOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    /// <exception cref="EmailSendException">Non-2xx answer, timeout or network failure.</exception>
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var body = new BrevoRequest(
            new BrevoAddress(_options.FromAddress, string.IsNullOrWhiteSpace(_options.FromName) ? null : _options.FromName),
            [new BrevoAddress(message.ToAddress, string.IsNullOrWhiteSpace(message.ToName) ? null : message.ToName)],
            message.Subject,
            message.HtmlContent,
            message.TextContent);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("api-key", _options.ApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = JsonContent.Create(body, options: JsonOptions);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new EmailSendException($"Brevo answered HTTP {(int)response.StatusCode}.");
            }
        }
        catch (EmailSendException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EmailSendException("Brevo did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new EmailSendException("Could not reach Brevo.", ex);
        }
    }

    private sealed record BrevoAddress(string Email, string? Name);

    private sealed record BrevoRequest(BrevoAddress Sender, BrevoAddress[] To, string Subject, string HtmlContent, string TextContent);
}
