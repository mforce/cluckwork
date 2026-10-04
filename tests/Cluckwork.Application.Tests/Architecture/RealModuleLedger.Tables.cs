namespace Cluckwork.Application.Tests.Architecture;

internal static partial class RealModuleLedger
{
    internal static readonly TableOwnerOverride[] TableOwnerOverrides =
    [
        new("AspNetRoleClaims",
            "Design 3.3 assigns Identity claim, join and token tables to Access; this framework CLR namespace is outside the Cluckwork namespace owner map."),
        new("AspNetUserClaims",
            "Design 3.3 assigns Identity claim, join and token tables to Access; this framework CLR namespace is outside the Cluckwork namespace owner map."),
        new("AspNetUserLogins",
            "Design 3.3 assigns Identity claim, join and token tables to Access; this framework CLR namespace is outside the Cluckwork namespace owner map."),
        new("AspNetUserRoles",
            "Design 3.3 assigns Identity claim, join and token tables to Access; this framework CLR namespace is outside the Cluckwork namespace owner map."),
        new("AspNetUserTokens",
            "Design 3.3 assigns Identity claim, join and token tables to Access; this framework CLR namespace is outside the Cluckwork namespace owner map."),
        new("UserRoleAssignments",
            "Design 3.3 assigns flock role assignments to Access; the CLR type remains in Cluckwork.Domain.Accounts, owned by Farm. No namespace move is authorized by this ledger."),
    ];
}
