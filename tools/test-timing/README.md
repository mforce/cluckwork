# Measure the integration suite

Run from the repository root with Python 3, .NET 10, and access to Docker.
Build separately so compilation does not enter the test wall clock:

```bash
/usr/bin/time -f 'build wall seconds: %e' dotnet build Cluckwork.sln
python3 tools/test-timing/measure.py /tmp/cluckwork-timing-before
```

Use a new output directory for each run. Additional arguments pass through to
`dotnet test`, for example `--configuration Release` after a Release build.
The script returns the test command's exit code. If this shell has a stale group
list but your account already belongs to `docker`, run the measurement through
`sg docker -c 'python3 tools/test-timing/measure.py /tmp/cluckwork-timing-before'`.

The output directory contains:

- `run.json`: command, UTC start, elapsed test-process wall time, and exit code.
- `results.trx`: per-test durations and outcomes.
- `console.log`: detailed console output prefixed with elapsed seconds.
- `docker-events.jsonl`: container lifecycle events during the run.
- `classes.csv`: test count, summed duration, and first/last test timestamps per class.
- `summary.json`: test totals, container readiness times, and the slowest classes.

Recreate the summary from those files with:

```bash
python3 tools/test-timing/summarize.py /tmp/cluckwork-timing-before
```

The measurement enables `CLUCKWORK_TEST_TIMING=1`. The base application factory
then reports its Postgres startup and host initialization plus migrations
separately. Derived-factory seeding is outside those phase timers. Factories
created inside test methods also contribute to TRX durations.

Container startup is the interval between Testcontainers' `created` and `ready`
console events. This excludes image pulls before creation. Docker events supply
the image names; only container IDs observed in this test process's console
contribute to the counts. Keep Docker events private because they can contain
metadata from other containers on the machine.

Summed durations describe work, not wall clock. Tests, fixture initialization,
and container readiness overlap. The interval-union fields count each occupied
second once, but those unions overlap each other too. Time outside both sets
includes discovery, teardown, fixture work, scheduling, and log delivery delays;
it is not a measurement of migrations alone. TRX timestamp spans include
per-test `IAsyncLifetime` setup and cleanup that the reported durations exclude.
Class spans omit class-fixture initialization and disposal. Shared-collection membership comes from the
current checkout's `Collection(IntegrationCollection.Name)` attributes.

Compare runs on the same machine, with the same configuration and image cache.
Repeat measurements because the default xUnit collection order varies by run.
Raw logs stay outside the repository; commit the findings, not runtime data.
