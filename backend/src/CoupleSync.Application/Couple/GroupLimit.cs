using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Couples;

internal static class GroupLimit
{
    public static void EnsureRoomForOneMore(int currentGroupCount)
    {
        if (currentGroupCount >= User.MaxGroups)
        {
            throw new ConflictException(
                "GROUP_LIMIT_REACHED",
                $"Você já participa de {User.MaxGroups} grupos, que é o máximo. Saia de um deles para entrar em outro.");
        }
    }
}
