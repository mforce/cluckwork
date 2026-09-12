#!/usr/bin/env bash
# #776 — measure backend test coverage. REPORT ONLY: nothing here gates a
# build, and nothing downstream of this script's output enforces a number.
# See docs/decisions/776-backend-coverage.md for why.
#
#   tools/coverage/collect.sh                 run all four projects, write
#                                              coverage-out/SUMMARY.md
#   tools/coverage/collect.sh --project Domain run one project only, for
#                                              local iteration (no combined
#                                              report, no SUMMARY.md)
#
# The organizing structure is the ROWS table below: one row per test
# project, each carrying its short name, its csproj path, and a one-sentence
# statement of what that project's coverage number actually means. Every
# step past the table iterates it. Adding a fifth test project means adding
# one row and nothing else.
set -euo pipefail

cd "$(dirname "$0")/../.."

# name|csproj|meaning
ROWS=(
  "Domain|tests/Cluckwork.Domain.Tests/Cluckwork.Domain.Tests.csproj|unit tests over Cluckwork.Domain alone; the number means asserted domain logic."
  "Application|tests/Cluckwork.Application.Tests/Cluckwork.Application.Tests.csproj|unit tests that also reference Application and Infrastructure, so those two assemblies appear at whatever incidental level the handler tests reach."
  "Integration|tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj|end-to-end over a real Postgres; it touches nearly every assembly incidentally, so a high line number here measures what is EXECUTED, not what is ASSERTED."
  "AppHost|tests/Cluckwork.AppHost.Tests/Cluckwork.AppHost.Tests.csproj|ten tests over the Aspire orchestration project."
)

RUNSETTINGS="tools/coverage/coverlet.runsettings"
OUT_ROOT="coverage-out"
RAW_ROOT="$OUT_ROOT/raw"
REPORT_ROOT="$OUT_ROOT/report"

PROJECT_FILTER=""
if [ "${1:-}" = "--project" ]; then
  PROJECT_FILTER="${2:?--project requires a name}"
fi

# Rows to run: all of them, or the one matching --project (case-insensitive).
SELECTED=()
for row in "${ROWS[@]}"; do
  name="${row%%|*}"
  if [ -z "$PROJECT_FILTER" ] || [ "$(echo "$name" | tr '[:upper:]' '[:lower:]')" = "$(echo "$PROJECT_FILTER" | tr '[:upper:]' '[:lower:]')" ]; then
    SELECTED+=("$row")
  fi
done
if [ -n "$PROJECT_FILTER" ] && [ "${#SELECTED[@]}" -eq 0 ]; then
  echo "No project matches --project '$PROJECT_FILTER'. Known: $(printf '%s ' "${ROWS[@]%%|*}")" >&2
  exit 1
fi

rm -rf "$RAW_ROOT"
mkdir -p "$RAW_ROOT" "$REPORT_ROOT"

echo "==> dotnet tool restore"
dotnet tool restore

echo "==> dotnet build Cluckwork.sln --configuration Release"
dotnet build Cluckwork.sln --configuration Release

# 1) Collect raw coverage per selected project.
for row in "${SELECTED[@]}"; do
  IFS='|' read -r name csproj _meaning <<<"$row"
  echo "==> dotnet test ($name)"
  dotnet test "$csproj" \
    --configuration Release --no-build \
    --settings "$RUNSETTINGS" \
    --collect:"XPlat Code Coverage" \
    --results-directory "$RAW_ROOT/$name"
done

# 2) Per-project report.
for row in "${SELECTED[@]}"; do
  IFS='|' read -r name _csproj _meaning <<<"$row"
  echo "==> reportgenerator ($name)"
  dotnet reportgenerator \
    "-reports:$RAW_ROOT/$name/**/coverage.cobertura.xml" \
    "-targetdir:$REPORT_ROOT/$name" \
    "-reporttypes:Html;MarkdownSummaryGithub;TextSummary" \
    "-title:Cluckwork $name coverage"
done

# A single-project run is for local iteration: stop after that project's own
# report. The combined report and SUMMARY.md below need every project's raw
# data to mean what they say.
if [ -n "$PROJECT_FILTER" ]; then
  echo "==> Report: $REPORT_ROOT/${SELECTED[0]%%|*}/index.html"
  exit 0
fi

# 3) Combined report — reportgenerator merges every project's cobertura file.
echo "==> reportgenerator (combined)"
dotnet reportgenerator \
  "-reports:$RAW_ROOT/**/coverage.cobertura.xml" \
  "-targetdir:$REPORT_ROOT/combined" \
  "-reporttypes:Html;MarkdownSummaryGithub;TextSummary" \
  "-title:Cluckwork combined coverage"

# 4) SUMMARY.md — concatenation only. Every number in it comes straight out
# of a generated SummaryGithub.md; this script never parses or re-derives a
# percentage.
SUMMARY="$OUT_ROOT/SUMMARY.md"
{
  cat <<'EOF'
# Backend coverage (#776)

This is a measurement, not a gate. Nothing in CI fails on these numbers.

Coverage says what is EXECUTED. It does not say what is ASSERTED, and it
never says what is REDUNDANT. Two tests can cover identical lines while only
one catches a defect. On this repository #771 found three guards that were
fully covered and still survived the exact mutations they were named for.
The tool for the redundancy question is mutation testing, and that is a
separate issue.

Read `Integration` with that in mind specifically. Those tests drive the API
over a real Postgres, so they touch a great deal of code incidentally. Its
line number will look like the strongest in the table and it is the weakest
evidence of assertion in the table.

Per-project is the primary view. The combined figure is here to answer "what
is untested anywhere", and it deliberately averages a unit-tested Domain
against an end-to-end Integration suite, so do not quote it as a quality
score.

Generated EF migration code is excluded (39,088 of the 76,977 lines of C# under src/).
Auto-properties are counted. No other filter is applied.

EOF

  for row in "${ROWS[@]}"; do
    IFS='|' read -r name _csproj meaning <<<"$row"
    echo "## $name"
    echo
    echo "$meaning"
    echo
    cat "$REPORT_ROOT/$name/SummaryGithub.md"
    echo
  done

  echo "## Combined"
  echo
  cat "$REPORT_ROOT/combined/SummaryGithub.md"
} >"$SUMMARY"

echo "==> Wrote $SUMMARY"
