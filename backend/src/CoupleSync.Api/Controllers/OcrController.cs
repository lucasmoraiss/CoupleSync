using System.Security.Claims;
using CoupleSync.Api.Contracts.Ocr;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Api.Filters;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.OcrImport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Controllers;

[ApiController]
[Authorize]
[RequireCouple]
[Route("api/v1/ocr")]
public sealed class OcrController : ControllerBase
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

    private readonly ImportJobService _importJobService;

    public OcrController(ImportJobService importJobService)
    {
        _importJobService = importJobService;
    }

    /// <summary>Upload a receipt/statement image or PDF for OCR processing.</summary>
    /// <remarks>
    /// Accepted MIME types (detected via magic bytes): image/jpeg, image/png, application/pdf.
    /// Maximum file size: 10 MB.
    /// </remarks>
    [HttpPost("upload")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    [ProducesResponseType(typeof(UploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413RequestEntityTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<ActionResult<UploadResponse>> Upload(IFormFile file, CancellationToken ct, [FromForm] bool aiCategorizationConsent = false)
    {
        if (file is null || file.Length == 0)
            throw new BadRequestException("FILE_REQUIRED", "Envie um arquivo.");

        if (file.Length > MaxFileSizeBytes)
            throw new AppException("FILE_TOO_LARGE", "O arquivo deve ter no máximo 10 MB.", StatusCodes.Status413RequestEntityTooLarge);

        // Detect MIME type from magic bytes — do NOT trust Content-Type header.
        using var fileStream = file.OpenReadStream();
        var header = new byte[8];
        var bytesRead = await fileStream.ReadAsync(header, 0, header.Length, ct);
        fileStream.Position = 0;

        var detectedMime = FileTypeDetector.DetectMimeType(header.AsSpan(0, bytesRead));
        if (detectedMime is null)
            throw new AppException("UNSUPPORTED_FILE_TYPE", "Tipos de arquivo aceitos: JPEG, PNG e PDF.", StatusCodes.Status415UnsupportedMediaType);

        var coupleId = GetAuthenticatedCoupleId();
        var userId = GetAuthenticatedUserId();

        var uploadId = await _importJobService.UploadAsync(
            coupleId, userId, fileStream, detectedMime, ct, file.FileName, aiCategorizationConsent);

        return Ok(new UploadResponse(uploadId));
    }

    private Guid GetAuthenticatedCoupleId()
    {
        var claimValue = User.FindFirstValue("couple_id");
        if (!Guid.TryParse(claimValue, out var coupleId))
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        return coupleId;
    }

    private Guid GetAuthenticatedUserId()
    {
        var claimValue = User.FindFirstValue("user_id");
        if (!Guid.TryParse(claimValue, out var userId))
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        return userId;
    }

    /// <summary>The couple's imports that still have lines waiting for review (to reopen them).</summary>
    [HttpGet("open")]
    [ProducesResponseType(typeof(OcrOpenImportsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OcrOpenImportsResponse>> GetOpenImports(CancellationToken ct)
    {
        var coupleId = GetAuthenticatedCoupleId();
        var open = await _importJobService.GetOpenImportsAsync(coupleId, ct);

        return Ok(new OcrOpenImportsResponse(open
            .Select(i => new OcrOpenImportResponse(
                i.UploadId, i.FileName, i.CreatedAtUtc, i.PendingLines, i.TotalLines, i.CreditsCount))
            .ToList()));
    }

    /// <summary>Get the processing status of an OCR job.</summary>
    [HttpGet("{uploadId:guid}/status")]
    [ProducesResponseType(typeof(OcrStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OcrStatusResponse>> GetStatus(Guid uploadId, CancellationToken ct)
    {
        var coupleId = GetAuthenticatedCoupleId();
        var job = await _importJobService.GetJobAsync(uploadId, coupleId, ct);
        if (job is null)
            throw new NotFoundException("OCR_JOB_NOT_FOUND", "Importação não encontrada.");

        return Ok(new OcrStatusResponse(job.Status.ToString(), job.ErrorCode, job.QuotaResetDate));
    }

    /// <summary>Get the OCR candidate list when the job is Ready.</summary>
    [HttpGet("{uploadId:guid}/results")]
    [ProducesResponseType(typeof(OcrResultsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OcrResultsResponse>> GetResults(Guid uploadId, CancellationToken ct)
    {
        var coupleId = GetAuthenticatedCoupleId();
        var review = await _importJobService.GetReviewAsync(uploadId, coupleId, ct);
        if (review is null)
            throw new NotFoundException("OCR_JOB_NOT_FOUND", "Importação não encontrada.");

        var response = new OcrResultsResponse(
            review.Lines.Select(l =>
            {
                var c = l.Candidate;
                return new OcrCandidateResponse(
                    c.Index, c.Date, c.Description, c.Amount,
                    c.Currency, c.Confidence, c.DuplicateSuspected,
                    c.SuggestedCategory is null ? null : TransactionCategories.NormalizeOrOther(c.SuggestedCategory),
                    l.State.ToString());
            }).ToList(),
            review.Credits.Select(c => new OcrCreditResponse(c.Date, c.Description, c.Amount, c.Currency)).ToList(),
            review.Credits.Count);

        return Ok(response);
    }

    /// <summary>Confirm selected OCR candidates and create Transaction records.</summary>
    [HttpPost("{uploadId:guid}/confirm")]
    [ProducesResponseType(typeof(ConfirmResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ConfirmResponse>> Confirm(
        Guid uploadId, [FromBody] ConfirmRequest request, CancellationToken ct)
    {
        var coupleId = GetAuthenticatedCoupleId();
        var userId = GetAuthenticatedUserId();

        var overrides = request.CategoryOverrides?
            .ToDictionary(o => o.Index, o => o.Category);

        var edits = request.CandidateEdits?
            .ToDictionary(e => e.Index, e => new CandidateEdit(e.Description, e.Amount));

        var created = await _importJobService.ConfirmCandidatesAsync(
            uploadId, coupleId, userId, request.SelectedIndices, overrides, ct, edits,
            request.KeepJobOpen, request.DiscardedIndices);

        if (created is null)
            throw new NotFoundException("OCR_JOB_NOT_FOUND", "Importação não encontrada.");

        return Ok(new ConfirmResponse(created.Created.Count, created.DuplicatesSkipped, created.RemainingLines));
    }
}
