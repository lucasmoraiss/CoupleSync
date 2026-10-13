namespace CoupleSync.Application.Common.Exceptions;

/// <summary>The closed set of failures of the Pluggy API, as the app sees them.</summary>
public static class PluggyErrorCodes
{
    public const string InvalidCredentials = "PLUGGY_INVALID_CREDENTIALS";
    public const string ItemNotFound = "PLUGGY_ITEM_NOT_FOUND";
    public const string ItemNeedsAction = "PLUGGY_ITEM_NEEDS_ACTION";
    public const string Unavailable = "PLUGGY_UNAVAILABLE";
    public const string RateLimited = "PLUGGY_RATE_LIMITED";

    /// <summary>
    /// Pluggy answered, and refused the request itself (a 4xx or 3xx nothing else explains). Why is not known here
    /// (the request, the plan, the state of the item...): the message only says what is true in every case.
    /// </summary>
    public const string Refused = "PLUGGY_REFUSED";
}

/// <summary>
/// A call to Pluggy failed in one of the known ways. Each code has one Portuguese message and one HTTP status.
/// Never 401: for the app a 401 of this API means "the session expired".
/// </summary>
public sealed class PluggyException : AppException
{
    public PluggyException(string code, string? callKind = null, int? httpStatus = null, string? pluggyCode = null)
        : base(code, MessageFor(code), StatusFor(code))
    {
        CallKind = callKind;
        HttpStatus = httpStatus;
        PluggyCode = pluggyCode;
    }

    /// <summary>Which call failed ("auth", "item", "accounts", "transactions", "bills", "item-update"), when a call did.</summary>
    public string? CallKind { get; }

    /// <summary>The HTTP status Pluggy answered with; null when there was no answer (network, timeout).</summary>
    public int? HttpStatus { get; }

    /// <summary>
    /// <c>code</c> and <c>codeDescription</c> of Pluggy's error body ("410/ENDPOINT_DEPRECATED"), cut and cleaned: for
    /// the log and the diary of the run, never a text of Pluggy shown as it came.
    /// </summary>
    public string? PluggyCode { get; }

    public static string MessageFor(string code) => code switch
    {
        PluggyErrorCodes.InvalidCredentials =>
            "O Pluggy recusou o Client ID ou o Client Secret. Confira os dois na aba \"Aplicação\" do dashboard.pluggy.ai e tente de novo.",
        PluggyErrorCodes.ItemNotFound =>
            "O Pluggy não encontrou este Item ID. Copie de novo pelo menu de três pontos da conexão, na Demo da sua aplicação.",
        PluggyErrorCodes.ItemNeedsAction =>
            "Este banco precisa de uma ação sua no Meu Pluggy (entrar de novo ou autorizar). Resolva em meu.pluggy.ai e verifique outra vez.",
        PluggyErrorCodes.RateLimited =>
            "O Pluggy limitou as consultas por enquanto. Aguarde alguns minutos e tente de novo.",
        PluggyErrorCodes.Unavailable =>
            "O Pluggy não respondeu agora. Tente de novo em alguns minutos.",
        PluggyErrorCodes.Refused =>
            "O Pluggy recusou este pedido. Tentar de novo agora não deve resolver.",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown Pluggy error code."),
    };

    private static int StatusFor(string code) => code switch
    {
        PluggyErrorCodes.InvalidCredentials => 422,
        PluggyErrorCodes.ItemNotFound => 404,
        PluggyErrorCodes.ItemNeedsAction => 422,
        PluggyErrorCodes.RateLimited => 429,
        _ => 502,
    };
}
