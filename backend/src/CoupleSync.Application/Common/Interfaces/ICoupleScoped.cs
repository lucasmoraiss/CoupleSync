namespace CoupleSync.Application.Common.Interfaces;

/// <summary>
/// Backward-compatibility alias. ICoupleScoped has been moved to CoupleSync.Domain.Interfaces.
/// New code should reference CoupleSync.Domain.Interfaces.ICoupleScoped directly.
/// </summary>
public interface ICoupleScoped : CoupleSync.Domain.Interfaces.ICoupleScoped { }
