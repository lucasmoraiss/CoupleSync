namespace CoupleSync.Application.Common.Interfaces;

public interface ICoupleContext
{
    /// <summary>
    /// Returns the couple_id from the current JWT claim, or null if the user has no couple.
    /// </summary>
    Guid? CoupleId { get; }
}
