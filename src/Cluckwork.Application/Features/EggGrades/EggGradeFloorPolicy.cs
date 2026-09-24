namespace Cluckwork.Application.Features.EggGrades;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;

// #911 — who may put a low-stock floor on a grade. One place, because two
// handlers ask it and a screen asks the same question from the token: a second
// copy would let the Grades dialog offer a field the handler refuses.
//
// The rest of the grade catalog stays Owner + Manager (#73). Only the floor is
// farm configuration in #729's sense, so only an Owner moves it. An unresolved
// actor is refused: the floor is never set by a system caller today.
public static class EggGradeFloorPolicy
{
    public static bool MaySetFloor(ICurrentUser currentUser) =>
        currentUser.IsResolved
        && Roles.ResolveEffective(currentUser.Roles) == EffectiveAccountRole.Owner;
}
