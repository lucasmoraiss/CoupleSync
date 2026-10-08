namespace CoupleSync.Application.Common.Exceptions;

/// <summary>The closed set of failures of the Pluggy API, as the app sees them.</summary>
public static class PluggyErrorCodes
{
    public const string InvalidCredentials = "PLUGGY_INVALID_CREDENTIALS";
    public const string ItemNotFound = "PLUGGY_ITEM_NOT_FOUND";
    public const string ItemNeedsAction = "PLUGGY_ITEM_NEEDS_ACTION";
    public const string Unavailable = "PLUGGY_UNAVAILABLE";
    public const string RateLimited = "PLUGGY_RATE_LIMITED";
}

/// <summary>
/// A call to Pluggy failed in one of the known ways. Each code has one Portuguese message and one HTTP status.
/// Never 401: for the app a 401 of this API means "the session expired".
/// </summary>
public sealed class PluggyException : AppException
{
    public PluggyException(string code)
        : base(code, MessageFor(code), StatusFor(code))
    {
    }

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
