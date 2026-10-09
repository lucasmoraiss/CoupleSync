namespace CoupleSync.Application.Common.Interfaces;

/// <summary>
/// The Pluggy API (https://api.pluggy.ai) as far as the app uses it. Every failure leaves as a
/// <see cref="Exceptions.PluggyException"/> with one of the closed codes.
/// </summary>
public interface IPluggyClient
{
    /// <summary>POST /auth: checks the credentials with Pluggy. Throws PLUGGY_INVALID_CREDENTIALS when they are refused.</summary>
    Task<PluggyAuth> AuthenticateAsync(string clientId, string clientSecret, CancellationToken ct);

    /// <summary>GET /items/{id}. Throws PLUGGY_ITEM_NOT_FOUND when Pluggy does not know the item.</summary>
    Task<PluggyItem> GetItemAsync(PluggyAuth auth, string itemId, CancellationToken ct);

    /// <summary>GET /accounts?itemId=. An item without accounts gives an empty list.</summary>
    Task<IReadOnlyList<PluggyAccount>> GetAccountsAsync(PluggyAuth auth, string itemId, CancellationToken ct);

    /// <summary>
    /// GET /transactions?accountId=&amp;from=&amp;to=, every page until the last one. Both days are inclusive, as Pluggy
    /// reads them (UTC calendar days).
    /// </summary>
    Task<IReadOnlyList<PluggyTransaction>> GetTransactionsAsync(PluggyAuth auth, string accountId, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>PATCH /items/{id}: asks Pluggy to read the bank again. Pluggy answers at once; the reading takes its time.</summary>
    Task RequestItemUpdateAsync(PluggyAuth auth, string itemId, CancellationToken ct);

    /// <summary>Drops the API key kept for a connection (its credentials changed or were erased).</summary>
    void ForgetConnection(Guid connectionId);
}

/// <summary>
/// Who is calling Pluggy. With a <see cref="ConnectionId"/> the API key obtained from Pluggy is reused between calls
/// for as long as it is valid. Deliberately not a record: nothing prints the secret by accident.
/// </summary>
public sealed class PluggyAuth
{
    private PluggyAuth(string clientId, string clientSecret, Guid? connectionId, string? credentialsVersion)
    {
        ClientId = clientId;
        ClientSecret = clientSecret;
        ConnectionId = connectionId;
        CredentialsVersion = credentialsVersion;
    }

    public string ClientId { get; }
    public string ClientSecret { get; }
    public Guid? ConnectionId { get; }

    /// <summary>
    /// What tells one set of stored credentials of the connection from another: the encrypted secret as stored
    /// (every encryption gives another text). An API key obtained with other credentials is never reused.
    /// </summary>
    public string? CredentialsVersion { get; }

    /// <summary>Credentials that belong to no stored connection yet (the test of the wizard).</summary>
    public static PluggyAuth Of(string clientId, string clientSecret) => new(clientId, clientSecret, null, null);

    /// <summary>The credentials of a stored connection; <paramref name="storedEncryptedSecret"/> is the secret as it is stored.</summary>
    public static PluggyAuth ForConnection(Guid connectionId, string clientId, string clientSecret, string storedEncryptedSecret)
        => new(clientId, clientSecret, connectionId, storedEncryptedSecret);

    public override string ToString() => ConnectionId is { } id ? $"PluggyAuth(connection {id})" : "PluggyAuth(unsaved)";
}

public static class PluggyItemStatus
{
    public const string Updated = "UPDATED";
    public const string Updating = "UPDATING";
    public const string LoginError = "LOGIN_ERROR";
    public const string WaitingUserInput = "WAITING_USER_INPUT";
    public const string Outdated = "OUTDATED";
}

public sealed record PluggyItem(
    string Id,
    string ConnectorName,
    string Status,
    string? ExecutionStatus,
    DateTime? UpdatedAtUtc,
    string? ErrorMessage);

public sealed record PluggyAccount(
    string Id,
    string Type,
    string? Subtype,
    string Name,
    string? MarketingName,
    string? Number,
    decimal Balance,
    string? CurrencyCode,
    PluggyCreditData? CreditData);

public sealed record PluggyCreditData(
    string? Level,
    string? Brand,
    DateOnly? BalanceCloseDate,
    DateOnly? BalanceDueDate,
    decimal? AvailableCreditLimit,
    decimal? CreditLimit,
    decimal? MinimumPayment);

/// <summary>
/// One transaction as Pluggy lists it. <see cref="RawJson"/> is the element exactly as received. Prints nothing of
/// its content (a record would print the description and the raw JSON).
/// </summary>
public sealed record PluggyTransaction(
    string Id,
    DateTime DateUtc,
    decimal Amount,
    string? Type,
    string? CurrencyCode,
    string? Description,
    string? DescriptionRaw,
    string? Category,
    string? CategoryId,
    string? MerchantName,
    string? MerchantCnpj,
    string? MerchantCategory,
    string? PaymentMethod,
    int? InstallmentNumber,
    int? TotalInstallments,
    string? BillId,
    string? Status,
    decimal? Balance,
    string RawJson)
{
    public override string ToString() => nameof(PluggyTransaction);
}
