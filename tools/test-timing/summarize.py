#!/usr/bin/env python3
"""Summarize a timestamped integration run captured by measure.py."""

import collections
import csv
import datetime
import json
import pathlib
import re
import sys
import xml.etree.ElementTree as ET


def seconds(value):
    hours, minutes, seconds = map(float, value.split(":"))
    return hours * 3600 + minutes * 60 + seconds


def union_seconds(intervals):
    end = float("-inf")
    total = 0
    for start, stop in sorted(intervals):
        total += max(0, stop - max(start, end))
        end = max(end, stop)
    return total


def summarize(directory):
    run = json.loads((directory / "run.json").read_text())
    epoch = datetime.datetime.fromisoformat(run["started_utc"]).timestamp()
    root = ET.parse(directory / "results.trx").getroot()
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    classes = {}
    for test in root.findall(".//t:UnitTest", ns):
        classes[test.attrib["id"]] = test.find("t:TestMethod", ns).attrib["className"]

    groups = collections.defaultdict(list)
    intervals = []
    outcomes = collections.Counter()
    for result in root.findall(".//t:UnitTestResult", ns):
        attrs = result.attrib
        outcomes[attrs["outcome"]] += 1
        start, stop = (
            datetime.datetime.fromisoformat(attrs[key]).timestamp() - epoch
            for key in ("startTime", "endTime")
        )
        intervals.append((start, stop))
        groups[classes[attrs["testId"]]].append(
            (seconds(attrs["duration"]), start, stop)
        )

    rows = sorted(
        ((name, len(tests), sum(t[0] for t in tests),
          min(t[1] for t in tests), max(t[2] for t in tests))
         for name, tests in groups.items()),
        key=lambda row: row[2], reverse=True,
    )
    shared_classes = set()
    for source in pathlib.Path("tests/Cluckwork.Api.IntegrationTests").rglob("*.cs"):
        shared_classes.update(re.findall(
            r"\[Collection\(IntegrationCollection.Name\)\]\s*public (?:sealed )?class (\w+)",
            source.read_text(),
        ))
    shared = [row for row in rows if row[0].rsplit(".", 1)[-1] in shared_classes]
    with (directory / "classes.csv").open("w") as output:
        writer = csv.writer(output)
        writer.writerow(["class", "tests", "test_seconds", "first_start", "last_end"])
        writer.writerows(rows)

    lifecycle = collections.defaultdict(dict)
    fixture_phases = collections.defaultdict(list)
    for line in (directory / "console.log").read_text().splitlines():
        match = re.match(r"([\d.]+)\t.*Docker container (\w+) (created|ready)$", line)
        if match:
            timestamp, container, event = match.groups()
            lifecycle[container][event] = float(timestamp)
        match = re.search(r"\[fixture-timing\] (\w+) ([\w-]+) ([\d.]+)$", line)
        if match:
            factory, phase, duration = match.groups()
            fixture_phases[f"{factory}/{phase}"].append(float(duration))

    images = {}
    for line in (directory / "docker-events.jsonl").read_text().splitlines():
        event = json.loads(line)
        if event.get("Action") == "create":
            actor = event["Actor"]
            images[actor["ID"][:12]] = actor["Attributes"]["image"]

    container_intervals = []
    container_groups = collections.defaultdict(list)
    for container, times in lifecycle.items():
        if "created" in times and "ready" in times:
            interval = times["created"], times["ready"]
            container_intervals.append(interval)
            container_groups[images.get(container, "unknown")].append(interval[1] - interval[0])

    report = {
        **run,
        "outcomes": dict(outcomes),
        "test_duration_sum_seconds": sum(row[2] for row in rows),
        "test_interval_union_seconds": union_seconds(intervals),
        "shared_collection": {
            "classes": len(shared),
            "tests": sum(row[1] for row in shared),
            "test_seconds": sum(row[2] for row in shared),
            "first_start": min((row[3] for row in shared), default=None),
            "last_end": max((row[4] for row in shared), default=None),
        },
        "containers_created": len(lifecycle),
        "containers_missing_ready": sum("ready" not in t for t in lifecycle.values()),
        "container_startup_union_seconds": union_seconds(container_intervals),
        "outside_test_and_container_intervals_seconds": run["wall_seconds"] - union_seconds(intervals + container_intervals),
        "container_startup_by_image": {
            reference: {"count": len(values), "sum_seconds": sum(values),
                        "mean_seconds": sum(values) / len(values)}
            for reference, values in container_groups.items()
        },
        "factory_phase_seconds": {
            phase: {"count": len(values), "sum_seconds": sum(values)}
            for phase, values in fixture_phases.items()
        },
        "slowest_classes": [dict(zip(
            ["class", "tests", "test_seconds", "first_start", "last_end"], row
        )) for row in rows[:15]],
    }
    (directory / "summary.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    summarize(pathlib.Path(sys.argv[1]))
