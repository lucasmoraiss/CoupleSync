using CoupleSync.Api.Contracts.Chat;
using CoupleSync.Api.Filters;
using CoupleSync.Application.AiChat;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace CoupleSync.Api.Controllers;

[ApiController]
[Authorize]
[RequireCouple]
[Route("api/v1/ai")]
public sealed class ChatController : ControllerBase
{
    private readonly AssistantChatService _chatService;

    public ChatController(AssistantChatService chatService)
    {
        _chatService = chatService;
    }

    /// <summary>
    /// Send a message to the AI financial assistant. 404 AI_CHAT_DISABLED while the AI is not available on the server,
    /// 403 AI_CONSENT_REQUIRED while the group has not switched it on, 429 when a budget of the day is used up.
    /// </summary>
    [HttpPost("chat")]
    [ProducesResponseType(typeof(ChatResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ChatResponse>> Chat(
        [FromBody] ChatRequest request,
        CancellationToken ct)
    {
        var coupleId = GetAuthenticatedCoupleId();

        var history = request.History?
            .Select(h => new ChatMessage(h.Role, h.Content))
            .ToList() ?? new List<ChatMessage>();

        var reply = await _chatService.ChatAsync(coupleId, request.Message, history, ct);
        return Ok(new ChatResponse(reply.Reply, reply.Provider));
    }

    private Guid GetAuthenticatedCoupleId()
    {
        var claimValue = User.FindFirstValue("couple_id");
        if (!Guid.TryParse(claimValue, out var coupleId))
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        return coupleId;
    }
}
