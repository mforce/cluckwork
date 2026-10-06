#!/usr/bin/env bash
# #1116 — print every CW1004 diagnostic: a module or adapter naming another module's non-contract type. The rule
# ships at Info, which a console build does not print, so this rebuilds each src project that runs the module-edge
# analyzer with a SARIF error log and lists the rows. Each cleanup PR quotes the count before and after; CW1004
# becomes an Error once it reaches zero.
#
#   tools/architecture/cw1004-census.sh    one "path:line<TAB>message" row per diagnostic, then the total
set -euo pipefail

cd "$(dirname "$0")/../.."
out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT

dotnet build src/Cluckwork.Api/Cluckwork.Api.csproj -v:q -nologo >/dev/null
for project in $(grep -l 'Cluckwork.Analyzers.csproj' src/*/*.csproj | grep -v '^src/Cluckwork.Analyzers/'); do
  dotnet build "$project" --no-incremental --no-dependencies -v:q -nologo \
    "-p:ErrorLog=$out/$(basename "$project" .csproj).sarif%2Cversion=2.1" >/dev/null
done

jq -r --arg root "file://$PWD/" '.runs[].results[] | select(.ruleId == "CW1004")
  | .locations[0].physicalLocation as $at
  | "\($at.artifactLocation.uri | ltrimstr($root)):\($at.region.startLine)\t\(.message.text)"' "$out"/*.sarif \
  | sort -u > "$out/rows"
cat "$out/rows"
echo "CW1004: $(wc -l < "$out/rows") diagnostics"
