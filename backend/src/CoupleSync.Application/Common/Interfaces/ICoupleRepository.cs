using CoupleEntityType = CoupleSync.Domain.Entities.Couple;
using UserEntityType = CoupleSync.Domain.Entities.User;

namespace CoupleSync.Application.Common.Interfaces;

public interface ICoupleRepository
{
    Task<UserEntityType?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken);

    Task<CoupleEntityType?> FindByJoinCodeAsync(string joinCode, CancellationToken cancellationToken);

    Task<CoupleEntityType?> FindByIdWithMembersAsync(Guid coupleId, CancellationToken cancellationToken);

    Task<bool> JoinCodeExistsAsync(string joinCode, CancellationToken cancellationToken);

    Task AddCoupleAsync(CoupleEntityType couple, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}