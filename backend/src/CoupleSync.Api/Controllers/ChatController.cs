using CoupleSync.Api.Contracts.Chat;
using CoupleSync.Api.Filters;
using CoupleSync.Application.AiChat;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Infrastructure.Integrations.Gemini;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace CoupleSync.Api.Controllers;

[ApiController]
[Authorize]
[RequireCouple]
[Route("api/v1/ai")]
public sealed class ChatController : ControllerBase
{
    private readonly GeminiChatService _chatService;
    private readonly GeminiOptions _geminiOptions;

    public ChatController(GeminiChatService chatService, IOptions<GeminiOptions> geminiOptions)
    {
        _chatService = chatService;
        _geminiOptions = geminiOptions.Value;
    }

    /// <summary>Send a message to the AI financial assistant.</summary>
    [HttpPost("chat")]
    [ProducesResponseType(typeof(ChatResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ChatResponse>> Chat(
        [FromBody] ChatRequest request,
        CancellationToken ct)
    {
        if (!IsAiChatEnabled())
            return NotFound(new { code = "AI_CHAT_DISABLED", message = "AI Chat is not available." });

        var coupleId = GetAuthenticatedCoupleId();

        var history = request.History?
            .Select(h => new ChatMessage(h.Role, h.Content))
            .ToList() ?? new List<ChatMessage>();

        var reply = await _chatService.ChatAsync(coupleId, request.Message, history, ct);
        return Ok(new ChatResponse(reply));
    }

    private bool IsAiChatEnabled() => _geminiOptions.Enabled;

    private Guid GetAuthenticatedCoupleId()
    {
        var claimValue = User.FindFirstValue("couple_id");
        if (!Guid.TryParse(claimValue, out var coupleId))
            throw new UnauthorizedException("UNAUTHORIZED", "Invalid or expired couple context.");
        return coupleId;
    }
}
