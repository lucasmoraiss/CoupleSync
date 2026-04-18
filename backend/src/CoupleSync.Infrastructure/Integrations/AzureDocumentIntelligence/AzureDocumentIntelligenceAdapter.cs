using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CoupleSync.Infrastructure.Integrations.AzureDocumentIntelligence;

/// <summary>
/// Sends documents to Azure AI Document Intelligence (prebuilt-receipt model) via REST API.
/// When AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT is absent, falls back to a stub response for
/// development and testing.
/// </summary>
public sealed class AzureDocumentIntelligenceAdapter : IOcrProvider
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AzureDocumentIntelligenceAdapter> _logger;
    private readonly HttpClient _httpClient;

    public AzureDocumentIntelligenceAdapter(
        IConfiguration configuration,
        ILogger<AzureDocumentIntelligenceAdapter> logger,
        IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("AzureDocumentIntelligence");
    }

    public async Task<string> AnalyzeAsync(string storagePath, string mimeType, CancellationToken ct)
    {
        var endpoint = _configuration["AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT"];
        var key = _configuration["AZURE_DOCUMENT_INTELLIGENCE_KEY"];

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key))
        {
            _logger.LogWarning("Azure Document Intelligence not configured. Using stub response.");
            return GetStubResponse();
        }

        var basePath = _configuration["Storage:BasePath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");

        // storagePath is like "uploads/{coupleId}/{uploadId}.ext" — strip leading "uploads/" prefix
        var relativePath = storagePath.StartsWith("uploads/", StringComparison.Ordinal)
            ? storagePath["uploads/".Length..]
            : storagePath;

        var fullPath = Path.GetFullPath(Path.Combine(basePath, relativePath));

        // Prevent path traversal: ensure resolved path stays within basePath
        var normalizedBase = Path.GetFullPath(basePath);
        if (!fullPath.StartsWith(normalizedBase + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !fullPath.Equals(normalizedBase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Storage path escapes the configured base directory.");
        }

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Uploaded file not found at storage path.", storagePath);

        var fileBytes = await File.ReadAllBytesAsync(fullPath, ct);
        var base64 = Convert.ToBase64String(fileBytes);

        var url = $"{endpoint.TrimEnd('/')}/documentintelligence/documentModels/prebuilt-receipt:analyze?api-version=2024-11-30";

        var requestBody = new { base64Source = base64 };
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        // Key added to header; never logged
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        request.Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(requestBody),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await _httpClient.SendAsync(request, ct);

        if ((int)response.StatusCode == 429)
        {
            DateTime? resetDate = null;
            if (response.Headers.RetryAfter?.Date != null)
                resetDate = response.Headers.RetryAfter.Date.Value.UtcDateTime;
            else if (response.Headers.RetryAfter?.Delta != null)
                resetDate = DateTime.UtcNow.Add(response.Headers.RetryAfter.Delta.Value);

            throw new OcrQuotaExhaustedException("Azure Document Intelligence quota exhausted.", resetDate);
        }

        response.EnsureSuccessStatusCode();

        // Azure returns 202 Accepted with Operation-Location for async analysis
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            var operationLocation = response.Headers.GetValues("Operation-Location").First();
            return await PollForResultAsync(operationLocation, key, ct);
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<string> PollForResultAsync(string operationLocation, string key, CancellationToken ct)
    {
        const int maxAttempts = 30;
        const int delayMs = 2000;

        for (int i = 0; i < maxAttempts; i++)
        {
            await Task.Delay(delayMs, ct);

            var request = new HttpRequestMessage(HttpMethod.Get, operationLocation);
            // Key added to header; never logged
            request.Headers.Add("Ocp-Apim-Subscription-Key", key);

            var response = await _httpClient.SendAsync(request, ct);

            if ((int)response.StatusCode == 429)
            {
                DateTime? resetDate = null;
                if (response.Headers.RetryAfter?.Date != null)
                    resetDate = response.Headers.RetryAfter.Date.Value.UtcDateTime;
                else if (response.Headers.RetryAfter?.Delta != null)
                    resetDate = DateTime.UtcNow.Add(response.Headers.RetryAfter.Delta.Value);

                throw new OcrQuotaExhaustedException("Azure Document Intelligence quota exhausted during polling.", resetDate);
            }

            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("status").GetString();

            if (status == "succeeded")
                return body;

            if (status == "failed")
                throw new InvalidOperationException("Azure OCR analysis failed.");
        }

        throw new TimeoutException($"Azure OCR analysis timed out after {maxAttempts * delayMs / 1000} seconds.");
    }

    /// <summary>
    /// Stub response returned when Azure credentials are not configured.
    /// Represents a two-item receipt for development and automated testing.
    /// </summary>
    private static string GetStubResponse()
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            status = "succeeded",
            analyzeResult = new
            {
                documents = new[]
                {
                    new
                    {
                        fields = new
                        {
                            Items = new
                            {
                                valueArray = new[]
                                {
                                    new
                                    {
                                        valueObject = new
                                        {
                                            Description = new { valueString = "Supermercado Compra" },
                                            TotalPrice = new { valueCurrency = new { amount = 150.50, currencyCode = "BRL" } }
                                        }
                                    },
                                    new
                                    {
                                        valueObject = new
                                        {
                                            Description = new { valueString = "Farmacia Medicamentos" },
                                            TotalPrice = new { valueCurrency = new { amount = 45.90, currencyCode = "BRL" } }
                                        }
                                    }
                                }
                            },
                            TransactionDate = new { valueDate = DateTime.UtcNow.ToString("yyyy-MM-dd") }
                        }
                    }
                }
            }
        });
    }
}
