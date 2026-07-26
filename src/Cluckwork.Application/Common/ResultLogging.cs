namespace Cluckwork.Application.Common;

using Cluckwork.Domain.Common;
using Microsoft.Extensions.Logging;

// #216 — expected failures surface as Warning with the failure reason, one
// consistent shape across every money-path handler (ErrorCode is the stable
// query key). Success transitions log Information in the handlers themselves.
public static class ResultLogging
{
    public static Result LogFailure(this Result result, ILogger logger, string operation)
    {
        if (result.IsFailure)
            logger.LogWarning("{Operation} failed: {ErrorCode} — {ErrorDescription}",
                operation, result.Error.Code, result.Error.Description);
        return result;
    }

    public static Result<T> LogFailure<T>(this Result<T> result, ILogger logger, string operation)
    {
        if (result.IsFailure)
            logger.LogWarning("{Operation} failed: {ErrorCode} — {ErrorDescription}",
                operation, result.Error.Code, result.Error.Description);
        return result;
    }
}
