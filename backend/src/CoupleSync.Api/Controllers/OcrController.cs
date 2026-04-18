using System.Security.Claims;
using CoupleSync.Api.Contracts.Ocr;
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
    public async Task<ActionResult<UploadResponse>> Upload(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { code = "FILE_REQUIRED", message = "A file must be provided." });

        if (file.Length > MaxFileSizeBytes)
            return StatusCode(
                StatusCodes.Status413RequestEntityTooLarge,
                new { code = "FILE_TOO_LARGE", message = "File must be 10 MB or less." });

        // Detect MIME type from magic bytes — do NOT trust Content-Type header.
        using var fileStream = file.OpenReadStream();
        var header = new byte[8];
        var bytesRead = await fileStream.ReadAsync(header, 0, header.Length, ct);
        fileStream.Position = 0;

        var detectedMime = FileTypeDetector.DetectMimeType(header.AsSpan(0, bytesRead));
        if (detectedMime is null)
            return StatusCode(
                StatusCodes.Status415UnsupportedMediaType,
                new { code = "UNSUPPORTED_FILE_TYPE", message = "Accepted file types: JPEG, PNG, PDF." });

        var coupleId = GetAuthenticatedCoupleId();
        var userId = GetAuthenticatedUserId();

        var uploadId = await _importJobService.UploadAsync(
            coupleId, userId, fileStream, detectedMime, ct);

        return Ok(new UploadResponse(uploadId));
    }

    private Guid GetAuthenticatedCoupleId()
    {
        var claimValue = User.FindFirstValue("couple_id");
        if (!Guid.TryParse(claimValue, out var coupleId))
            throw new UnauthorizedException("UNAUTHORIZED", "Invalid or expired couple context.");
        return coupleId;
    }

    private Guid GetAuthenticatedUserId()
    {
        var claimValue = User.FindFirstValue("user_id");
        if (!Guid.TryParse(claimValue, out var userId))
            throw new UnauthorizedException("UNAUTHORIZED", "Invalid or expired user context.");
        return userId;
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
            return NotFound(new { code = "OCR_JOB_NOT_FOUND", message = "Import job not found." });

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
        var candidates = await _importJobService.GetCandidatesAsync(uploadId, coupleId, ct);
        if (candidates is null)
            return NotFound(new { code = "OCR_JOB_NOT_FOUND", message = "Import job not found." });

        var response = new OcrResultsResponse(
            candidates.Select(c => new OcrCandidateResponse(
                c.Index, c.Date, c.Description, c.Amount,
                c.Currency, c.Confidence, c.DuplicateSuspected, c.SuggestedCategory)).ToList());

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

        var created = await _importJobService.ConfirmCandidatesAsync(
            uploadId, coupleId, userId, request.SelectedIndices, overrides, ct);

        if (created is null)
            return NotFound(new { code = "OCR_JOB_NOT_FOUND", message = "Import job not found." });

        return Ok(new ConfirmResponse(created.Count));
    }
}
