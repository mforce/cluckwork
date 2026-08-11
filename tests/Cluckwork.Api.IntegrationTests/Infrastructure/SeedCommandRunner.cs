namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using System.Diagnostics;

// Shared subprocess runner for the `seed --profile <name>` CLI tests
// (SeedCommandTests + SimulationSeedCommandTests). Extracted (#279 review) so
// the non-trivial pipe-draining/timeout logic lives in exactly one place rather
// than being duplicated per profile-test class.
//
// Two failure modes motivate the care here:
//  1. The exact regression these suites target: if the seed command fell
//     through into app.Run() instead of exiting, Kestrel binds and the process
//     never exits — and redirected stdout/stderr streams then never reach EOF
//     either, so a naive "wait then read" hangs forever with no useful message.
//  2. Draining only one of stdout/stderr risks a pipe-buffer deadlock: if the
//     child writes enough to the undrained stream to fill its OS pipe buffer, it
//     blocks on write while the test blocks on WaitForExitAsync — neither side
//     proceeds.
// This starts both drains concurrently with the wait, bounds the wait, and on
// timeout kills the whole process tree (`dotnet <dll>` can itself be a wrapper
// process) so the test fails fast with a clear message instead of hanging.
public static class SeedCommandRunner
{
    // timeoutMeaning: what a timeout tells the CALLER, as one or more COMPLETE
    // SENTENCES — it is spliced between two others, so it must start capitalized
    // and end with a period or the diagnostic reads as a run-on. The default
    // states the regression the seed/migrate suites are looking for — a verb
    // falling through into app.Run() instead of exiting. ProcessRoleGuardTests'
    // serving arms deliberately start a process with NO verb and expect it to die
    // on a boot guard, so for them a timeout means the exact opposite (the boot
    // succeeded), and the default message would send the reader hunting the wrong
    // bug.
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunToCompletionAsync(
        Process proc, TimeSpan timeout, string? timeoutMeaning = null)
    {
        using (proc)
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            var waitTask = proc.WaitForExitAsync();

            var completed = await Task.WhenAny(waitTask, Task.Delay(timeout));
            if (completed != waitTask)
            {
                try { proc.Kill(entireProcessTree: true); }
                catch { /* exited between the timeout firing and the kill call */ }

                // Killing the tree lets the redirected pipes reach EOF, so these
                // complete quickly now rather than hanging alongside the process.
                var partialStdout = await stdoutTask;
                var partialStderr = await stderrTask;
                Assert.Fail(
                    $"`{proc.StartInfo.FileName} {proc.StartInfo.Arguments}` did not exit within {timeout}. " +
                    (timeoutMeaning
                     ?? "This is the exact regression under test: falling through into app.Run() instead of "
                        + "returning would hang here.") +
                    $" Killed the process tree. stdout={partialStdout} stderr={partialStderr}");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (proc.ExitCode, stdout, stderr);
        }
    }
}
