using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// Runs an action before each of the first `times` commands whose text contains
// the armed fragment: a concurrent writer landing at an exact point, or
// requests meeting there. The last match disarms before its action runs, so
// that action's own commands pass through unhooked; with `times` above one, an
// earlier action's matching commands count.
public sealed class BeforeCommandHook : DbCommandInterceptor
{
    private sealed record Armed(string Fragment, Func<Task> Action);

    private Armed? _armed;
    private int _left;
    private int _fired;

    // Whether the armed action ran, so a test proves its race happened.
    public bool Fired => Volatile.Read(ref _fired) == 1;

    public bool IsArmed => Volatile.Read(ref _armed) is not null;

    public void Arm(string fragment, Func<Task> action, int times = 1)
    {
        Volatile.Write(ref _fired, 0);
        Volatile.Write(ref _left, times);
        Volatile.Write(ref _armed, new Armed(fragment, action));
    }

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
            || !command.CommandText.Contains(armed.Fragment, StringComparison.Ordinal))
            return;
        var left = Interlocked.Decrement(ref _left);
        if (left < 0)
            return;
        if (left == 0)
            Interlocked.CompareExchange(ref _armed, null, armed);
        await armed.Action();
        Volatile.Write(ref _fired, 1);
    }
}
