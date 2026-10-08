using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Domain.Entities;

public enum BankConnectionStatus
{
    Active,
    Error,
    Disconnected
}

/// <summary>
/// One person's link to their own Pluggy application (Open Finance through Meu Pluggy) inside one group: at most one
/// per person and group. The whole group sees it; only <see cref="UserId"/> changes it. The credentials are only ever
/// held encrypted, and are erased when the person disconnects.
/// </summary>
public sealed class BankConnection : ICoupleScoped
{
    public const string PluggyProvider = "PLUGGY";
    public const int MaxLabelLength = 60;
    public const int DefaultHistoryMonths = 3;
    public const int ClientIdHintLength = 4;
    public const int MaxErrorMessageLength = 512;

    public static readonly IReadOnlyList<int> AllowedHistoryMonths = [3, 6, 12];

    private BankConnection() { }

    public Guid Id { get; private set; }
    public Guid CoupleId { get; private set; }
    public Guid UserId { get; private set; }
    public string Provider { get; private set; } = PluggyProvider;
    public string Label { get; private set; } = string.Empty;

    /// <summary>Encrypted; never leaves the server. Null once disconnected.</summary>
    public string? ClientIdEncrypted { get; private set; }

    /// <summary>Encrypted; never leaves the server. Null once disconnected.</summary>
    public string? ClientSecretEncrypted { get; private set; }

    /// <summary>The last characters of the client id: all that is ever shown of it.</summary>
    public string? ClientIdHint { get; private set; }

    public BankConnectionStatus Status { get; private set; }
    public DateTime? LastSyncAtUtc { get; private set; }
    public string? LastErrorCode { get; private set; }
    public string? LastErrorMessage { get; private set; }

    /// <summary>How far back the first synchronisation goes (3, 6 or 12 months).</summary>
    public int HistoryMonths { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public bool HasCredentials => ClientIdEncrypted is not null && ClientSecretEncrypted is not null;

    public static BankConnection Create(
        Guid coupleId,
        Guid userId,
        string label,
        string clientIdEncrypted,
        string clientSecretEncrypted,
        string clientIdHint,
        int historyMonths,
        DateTime nowUtc)
    {
        var connection = new BankConnection
        {
            Id = Guid.NewGuid(),
            CoupleId = coupleId,
            UserId = userId,
            Provider = PluggyProvider,
            CreatedAtUtc = nowUtc,
        };
        connection.Connect(label, clientIdEncrypted, clientSecretEncrypted, clientIdHint, historyMonths, nowUtc);
        return connection;
    }

    /// <summary>Stores new credentials: on creation, and when a disconnected person connects again.</summary>
    public void Connect(
        string label,
        string clientIdEncrypted,
        string clientSecretEncrypted,
        string clientIdHint,
        int historyMonths,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Trim().Length > MaxLabelLength)
            throw new ArgumentException($"O apelido é obrigatório e deve ter no máximo {MaxLabelLength} caracteres.", nameof(label));
        if (string.IsNullOrWhiteSpace(clientIdEncrypted))
            throw new ArgumentException("O Client ID é obrigatório.", nameof(clientIdEncrypted));
        if (string.IsNullOrWhiteSpace(clientSecretEncrypted))
            throw new ArgumentException("O Client Secret é obrigatório.", nameof(clientSecretEncrypted));
        if (!AllowedHistoryMonths.Contains(historyMonths))
            throw new ArgumentException("O período deve ser de 3, 6 ou 12 meses.", nameof(historyMonths));

        Label = label.Trim();
        ClientIdEncrypted = clientIdEncrypted;
        ClientSecretEncrypted = clientSecretEncrypted;
        ClientIdHint = clientIdHint;
        HistoryMonths = historyMonths;
        Status = BankConnectionStatus.Active;
        LastErrorCode = null;
        LastErrorMessage = null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>The credentials are gone from this moment on; items and accounts stay.</summary>
    public void Disconnect(DateTime nowUtc)
    {
        ClientIdEncrypted = null;
        ClientSecretEncrypted = null;
        ClientIdHint = null;
        Status = BankConnectionStatus.Disconnected;
        LastErrorCode = null;
        LastErrorMessage = null;
        UpdatedAtUtc = nowUtc;
    }

    public void MarkError(string code, string message, DateTime nowUtc)
    {
        if (Status == BankConnectionStatus.Disconnected) return;
        Status = BankConnectionStatus.Error;
        LastErrorCode = code;
        LastErrorMessage = message.Length > MaxErrorMessageLength ? message[..MaxErrorMessageLength] : message;
        UpdatedAtUtc = nowUtc;
    }

    public void MarkWorking(DateTime nowUtc)
    {
        if (Status != BankConnectionStatus.Error) return;
        Status = BankConnectionStatus.Active;
        LastErrorCode = null;
        LastErrorMessage = null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>How far back an account never read before goes (the last step of the wizard): 3, 6 or 12 months.</summary>
    public void SetHistoryMonths(int historyMonths, DateTime nowUtc)
    {
        if (!AllowedHistoryMonths.Contains(historyMonths))
            throw new ArgumentException("O período deve ser de 3, 6 ou 12 meses.", nameof(historyMonths));
        if (HistoryMonths == historyMonths) return;
        HistoryMonths = historyMonths;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>A synchronisation ended well: the only thing that moves <see cref="LastSyncAtUtc"/>.</summary>
    public void MarkSynced(DateTime nowUtc)
    {
        if (Status == BankConnectionStatus.Disconnected) return;
        Status = BankConnectionStatus.Active;
        LastErrorCode = null;
        LastErrorMessage = null;
        LastSyncAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>What may be shown of a client id: its last characters.</summary>
    public static string HintOf(string clientId)
    {
        var trimmed = clientId.Trim();
        return trimmed.Length <= ClientIdHintLength ? trimmed : trimmed[^ClientIdHintLength..];
    }
}
