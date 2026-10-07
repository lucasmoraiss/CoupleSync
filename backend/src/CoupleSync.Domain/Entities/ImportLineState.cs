namespace CoupleSync.Domain.Entities;

/// <summary>Outcome of one debit line of an import review.</summary>
public enum ImportLineState
{
    Pending,
    Confirmed,
    Discarded
}
