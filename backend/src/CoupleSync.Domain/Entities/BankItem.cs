using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Domain.Entities;

/// <summary>One bank linked through a <see cref="BankConnection"/>: a Pluggy "item".</summary>
public sealed class BankItem : ICoupleScoped
{
    public const int MaxPluggyIdLength = 64;
    public const int MaxConnectorNameLength = 120;
    public const int MaxStatusLength = 32;
    public const int MaxErrorMessageLength = 512;

    private BankItem() { }

    public Guid Id { get; private set; }
    public Guid CoupleId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public string PluggyItemId { get; private set; } = string.Empty;

    /// <summary>The bank's name, as Pluggy calls the connector.</summary>
    public string ConnectorName { get; private set; } = string.Empty;

    /// <summary>Kept as Pluggy sends them (UPDATED, UPDATING, LOGIN_ERROR, WAITING_USER_INPUT, OUTDATED...).</summary>
    public string Status { get; private set; } = string.Empty;

    public string? ExecutionStatus { get; private set; }
    public DateTime? LastUpdatedAtUtc { get; private set; }
    public string? LastErrorMessage { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static BankItem Create(
        Guid coupleId,
        Guid connectionId,
        string pluggyItemId,
        string connectorName,
        string status,
        string? executionStatus,
        DateTime? lastUpdatedAtUtc,
        string? lastErrorMessage,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(pluggyItemId) || pluggyItemId.Trim().Length > MaxPluggyIdLength)
            throw new ArgumentException("O Item ID é inválido.", nameof(pluggyItemId));

        var item = new BankItem
        {
            Id = Guid.NewGuid(),
            CoupleId = coupleId,
            ConnectionId = connectionId,
            PluggyItemId = pluggyItemId.Trim(),
            CreatedAtUtc = nowUtc,
        };
        item.Refresh(connectorName, status, executionStatus, lastUpdatedAtUtc, lastErrorMessage);
        return item;
    }

    /// <summary>Takes what Pluggy says about the item now.</summary>
    public void Refresh(string connectorName, string status, string? executionStatus, DateTime? lastUpdatedAtUtc, string? lastErrorMessage)
    {
        ConnectorName = TextLimits.Clip(connectorName, MaxConnectorNameLength) ?? string.Empty;
        Status = TextLimits.Clip(status, MaxStatusLength) ?? string.Empty;
        ExecutionStatus = TextLimits.Clip(executionStatus, MaxStatusLength);
        LastUpdatedAtUtc = lastUpdatedAtUtc;
        LastErrorMessage = TextLimits.Clip(lastErrorMessage, MaxErrorMessageLength);
    }
}

/// <summary>Texts that come from outside (Pluggy) are cut to the size of their column instead of failing the write.</summary>
internal static class TextLimits
{
    public static string? Clip(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
