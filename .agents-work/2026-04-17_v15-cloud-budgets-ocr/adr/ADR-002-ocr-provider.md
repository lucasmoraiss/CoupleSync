# ADR-002 — OCR Provider Selection

**Session**: 2026-04-17_v15-cloud-budgets-ocr  
**Date**: April 2026  
**Status**: Accepted  
**Deciders**: User + Architect agent  

---

## Context

The OCR Import feature (FR-121–FR-131) requires a cloud service that can extract structured transaction data from photographed bank statements, printed receipts, and PDF exports. Requirements:

- Must extract at minimum: `date`, `description`, `amount`, `currency` from financial documents.
- Must handle multi-row tabular formats (bank statement printouts with many transactions per page).
- Must be free tier — 100% $0/month for pilot scale (< 100 uploads/month expected).
- Latency target: < 30 seconds per single-page document (NFR-121).
- Must be callable from a .NET 8 backend service via REST or SDK.
- Must integrate cleanly with Firebase Storage URLs or accept raw byte streams.

---

## Decision

**Primary OCR provider**: Azure AI Document Intelligence  
- Model: `prebuilt-invoice` or `prebuilt-layout` depending on document type.  
- Free tier: 5,000 pages/month, 2 requests/second.  
- Accessed via `AzureDocumentIntelligenceAdapter` behind `IOcrProvider` interface.

**Fallback OCR provider**: Google Cloud Vision  
- Activated only when `AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT` is absent and `GOOGLE_VISION_API_KEY` is present.  
- Free tier: 1,000 text-detection units/month.  
- Used as a graceful degradation path if Azure quota is exhausted.

**Self-hosted OCR (Tesseract)**: Rejected.

---

## Alternatives Considered

### Option A — Azure AI Document Intelligence ✅ CHOSEN (primary)

- **Free tier**: 5,000 pages/month — well above expected pilot volume (< 100 uploads/month).
- **Financial-document awareness**: `prebuilt-invoice` and `prebuilt-receipt` models are specifically trained on structured financial documents. They extract line items, amounts, dates, merchant names, and totals as structured fields, not just raw text.
- **Table extraction**: `prebuilt-layout` model handles tabular bank statements and produces a JSON table structure that maps cleanly to `OcrCandidate` objects.
- **Latency**: Async polling model; single-page documents typically complete in 3–15 s. Meets NFR-121 (< 30 s).
- **SDK**: Official `Azure.AI.FormRecognizer` / `Azure.AI.DocumentIntelligence` NuGet packages for .NET.
- **Limitation**: Azure subscription required (credit card for account creation, but no charges). If Azure account unavailable, fallback to Google Vision is automatic.

### Option B — Google Cloud Vision ✅ CHOSEN (fallback)

- **Free tier**: 1,000 text-detection units/month.
- **Capability**: General-purpose text extraction (OCR), not financial-document–aware. Returns raw text blocks; no structured field extraction. Requires custom parsing logic on top of raw OCR output.
- **Limitation**: 1,000 units/month is low; unsuitable as primary if more than 1,000 pages are processed.
- **Role in V1.5**: Fallback only, activated via `OcrOptions.ProcessingProvider = GoogleVision`. Custom parser in `GoogleCloudVisionAdapter` will attempt to extract transaction rows from raw text using regex heuristics.

### Option C — Tesseract (self-hosted)

- **Cost**: Free (open-source).
- **Accuracy**: 70–80% F1 on scanned bank statements without image preprocessing. Financial amounts and dates are a common failure point for standard Tesseract models.
- **Operational overhead**: Requires bundling `tesseract.exe` or running a sidecar container; increases Docker image size significantly; adds CPU overhead per request.
- **Verdict**: Rejected. Accuracy too low for financial data without significant preprocessing investment; operational cost not justified at pilot scale when Azure provides a better free-tier option.

### Option D — AWS Textract

- **Free tier**: 1,000 pages/month for first 3 months only (trial period), then per-page cost.
- **Verdict**: Rejected — not perpetually free; trial ends.

### Option E — Mindee

- **Free tier**: 250 API calls/month.
- **Capability**: Specifically designed for receipts and invoices; high accuracy.
- **Limitation**: 250 calls/month is very restrictive.
- **Verdict**: Rejected — quota too low; no fallback margin.

---

## Interface Contract

The OCR provider is hidden behind `IOcrProvider`:

```csharp
public interface IOcrProvider
{
    Task<OcrExtractionResult> ExtractAsync(
        string storageUrl,
        string mimeType,
        CancellationToken cancellationToken);
}

public record OcrExtractionResult(
    bool Success,
    IReadOnlyList<OcrCandidateDto> Candidates,
    string? ErrorCode,          // "quota_exhausted" | "provider_error" | null
    DateTimeOffset? QuotaResetDate);
```

Both `AzureDocumentIntelligenceAdapter` and `GoogleCloudVisionAdapter` implement `IOcrProvider`. The active implementation is resolved from DI based on `OcrOptions.ProcessingProvider` configuration.

---

## Quota Exhaustion Handling

When Azure AI Document Intelligence returns HTTP 429 or a quota-exceeded error:

1. `AzureDocumentIntelligenceAdapter` throws `OcrQuotaExhaustedException(quotaResetDate)`.
2. `ImportJobService` catches this exception, sets `ImportJob.status = Failed`, `error_code = quota_exhausted`, `quota_reset_date = <first of next month>`.
3. API endpoint returns `HTTP 429` with body:
   ```json
   {
     "code": "OCR_QUOTA_EXHAUSTED",
     "message": "OCR não disponível este mês. Tente novamente em 01/05/2026.",
     "quota_reset_date": "2026-05-01"
   }
   ```
4. Mobile surfaces this with a recovery card: "OCR unavailable — quota reached. Resets on [date]."

If `GOOGLE_VISION_API_KEY` is configured, `OcrProcessingService` can optionally retry with the fallback provider before returning 429 (configurable via `OcrOptions.AutoFallback = true`).

---

## Consequences

### Positive
- Azure AI Document Intelligence's prebuilt financial models dramatically reduce post-processing logic compared to raw OCR.
- The `IOcrProvider` abstraction means switching or adding providers requires no changes to `ImportJobService` or API controllers.
- 5,000 pages/month free quota provides a 50× safety margin over expected pilot volume.

### Negative / Risks
- Azure AI Document Intelligence requires an Azure subscription (credit card registration, no charges).
- If Azure subscription is unavailable, Google Vision fallback provides degraded accuracy.
- `prebuilt-layout` (for generic bank statement PDFs) may produce noisy table output; `OcrExtractionParser` will need custom filtering heuristics for Brazilian Portuguese bank statement formats.

---

## Notes

- Brazilian bank statement formats (Nubank, Itaú, Bradesco, Inter) should be tested during QA with real sample documents to calibrate confidence threshold filtering in `OcrExtractionParser`.
- Minimum confidence threshold for surfacing a candidate to the user: `0.6` (configurable via `OcrOptions.MinConfidenceThreshold`).
- Candidates below threshold are excluded from the review screen entirely (lost data acceptable at low confidence; user can add transactions manually).
