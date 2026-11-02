using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// Runs an action once, before the first command whose text contains the armed
// fragment: a concurrent writer landing at an exact point. The action's own
// commands pass through unhooked, because the hook disarms first.
public sealed class BeforeCommandHook : DbCommandInterceptor
{
    private sealed record Armed(string Fragment, Func<Task> Action);

    private Armed? _armed;

    public void Arm(string fragment, Func<Task> action) => Volatile.Write(ref _armed, new Armed(fragment, action));

    public void Disarm() => Volatile.Write(ref _armed, null);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await RunIfArmedAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await RunIfArmedAsync(command);
        return result;
    }

    private async Task RunIfArmedAsync(DbCommand command)
    {
        if (Volatile.Read(ref _armed) is not { } armed
            || !command.CommandText.Contains(armed.Fragment, StringComparison.Ordinal)
            || Interlocked.CompareExchange(ref _armed, null, armed) != armed)
            return;
        await armed.Action();
    }
}
