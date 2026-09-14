#!/usr/bin/env python3
"""Capture test wall clock, TRX durations, and Docker events. Build separately."""

import datetime
import json
import os
import pathlib
import subprocess
import sys
import time

from summarize import summarize


def main():
    directory = pathlib.Path(sys.argv[1]).resolve()
    directory.mkdir(parents=True, exist_ok=False)
    subprocess.run(["docker", "info"], check=True, stdout=subprocess.DEVNULL)
    command = [
        "dotnet", "test",
        "tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj",
        "--no-build", "--logger", "console;verbosity=detailed",
        "--logger", "trx;LogFileName=results.trx",
        "--results-directory", str(directory), *sys.argv[2:],
    ]
    with (directory / "docker-events.jsonl").open("w") as event_log:
        events = subprocess.Popen(
            ["docker", "events", "--filter", "type=container", "--format", "{{json .}}"],
            stdout=event_log,
        )
        start = time.time()
        timer = time.monotonic()
        try:
            with (directory / "console.log").open("w") as log:
                with subprocess.Popen(command, stdout=subprocess.PIPE,
                                      stderr=subprocess.STDOUT, text=True,
                                      env={**os.environ, "CLUCKWORK_TEST_TIMING": "1"}) as process:
                    for line in process.stdout:
                        log.write(f"{time.monotonic() - timer:.3f}\t{line}")
                        log.flush()
                    code = process.wait()
            elapsed = time.monotonic() - timer
        finally:
            events.terminate()
            events.wait()

    run = {
        "command": command,
        "started_utc": datetime.datetime.fromtimestamp(start, datetime.timezone.utc).isoformat(),
        "wall_seconds": elapsed,
        "exit_code": code,
    }
    (directory / "run.json").write_text(json.dumps(run, indent=2) + "\n")
    if (directory / "results.trx").exists():
        summarize(directory)
    else:
        print(json.dumps(run, indent=2))
    return code


if __name__ == "__main__":
    sys.exit(main())
