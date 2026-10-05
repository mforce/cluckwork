using Cluckwork.Domain.Common;

namespace Cluckwork.Application.Common;

public static class StepUpErrorCodes
{
    // #308 — every step-up rejection reason (missing, malformed, expired,
    // replayed, wrong-account/user, stamp-revoked, logout-revoked) maps to
    // this one code so a gated endpoint's caller cannot enumerate WHY a grant
    // failed.
    public const string Required = "Identity.StepUpRequired";
}
