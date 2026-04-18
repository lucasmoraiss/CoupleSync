namespace CoupleSync.Domain.Interfaces;

/// <summary>
/// Marker interface for all couple-scoped domain entities.
/// Any entity implementing this interface will automatically receive
/// a global EF Core query filter restricting results to the current couple.
/// </summary>
public interface ICoupleScoped
{
    Guid CoupleId { get; }
}
