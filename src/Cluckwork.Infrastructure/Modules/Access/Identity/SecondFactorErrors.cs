using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1274 "What a caller sees": the answers the code step, step-up and
// change-password share.
internal static class SecondFactorErrors
{
    internal static readonly Error Required = Error.Validation(
        SecondFactorErrorCodes.Required, "Enter the code from your authenticator app.");

    // One body for a wrong, reused or malformed code and a locked account, so
    // the answer never confirms that a guessed code was valid (G3.12).
    internal static readonly Error Incorrect = Error.Validation(
        SecondFactorErrorCodes.Incorrect, "That code did not work. If you just used it, wait for the next one.");

    internal static readonly Error KeyUnavailable = Error.Validation(
        SecondFactorErrorCodes.KeyUnavailable, "Your authenticator key cannot be read. Use a recovery code.");

    // Step-up and change-password (decision 12): every refusal is 400 or 429,
    // never 401, and a superseded proof reads as a wrong current password.
    internal static Error ForPrivilegedAction(FactorOutcome refused) => refused switch
    {
        FactorOutcome.Superseded => CurrentPasswordCheck.Incorrect,
        FactorOutcome.Rejected => Incorrect,
        FactorOutcome.Unavailable => KeyUnavailable,
        FactorOutcome.Throttled throttled => new RetryLaterError(throttled.RetryAfter),
        _ => throw new ArgumentOutOfRangeException(nameof(refused), "An accepted code is not a refusal."),
    };
}
