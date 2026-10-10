#!/usr/bin/env bash
#
# tools/mcp/mutation-check.sh — proves each #805 identity-bridge claim has a test that
# fails when the claim breaks (docs/plans/770-mcp-server/02-guards.md rows 1 to 7b).
#
# Same method as tools/oauth/mutation-check.sh: baseline green, then per mutant apply
# one exact-string edit (it must match once), rebuild, run the ONE named test with a
# TRX logger and judge from the TRX, never the exit code. Verdicts:
#
#   killed        the named test failed and its message carries the declared text
#   held          a "hold" mutant's test still passed
#   SURVIVED      a "kill" mutant's test passed
#   WRONG         the test failed without the declared text
#   INCONCLUSIVE  no TRX, no result, the mutant did not apply or did not compile
#
# Rows 6, 7 and 7b's mutations are tools a later PR would add, so they live as
# permanent fixtures in McpToolSurfaceTests and McpSecondaryScopeTests. The two walk
# mutants below remove the request-reader check, to show that its fixture and the
# real-host walk depend on it.
#
# Usage:  sg docker -c 'bash tools/mcp/mutation-check.sh'   (two tests boot the API)

set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

BRIDGE=src/Cluckwork.Api/Mcp/McpCallContext.cs
IDENTITY=src/Cluckwork.Api/Hosting/CluckworkIdentityServiceCollectionExtensions.cs
WALK=tests/Cluckwork.Api.IntegrationTests/Mcp/SecondaryScopeWalk.cs
TESTS=tests/Cluckwork.Api.IntegrationTests
TEST_NS=Cluckwork.Api.IntegrationTests.Mcp
SUITE="FullyQualifiedName~$TEST_NS."

# name # expect # file # find # replace # test # declared failure text
MUTANTS=$(cat <<'EOF'
no-http-context-check#kill#BRIDGE#httpContextAccessor.HttpContext\n            ?? throw Refused("no HttpContext is present");#httpContextAccessor.HttpContext!;#McpCallContextTests.NoHttpContext_Throws#Assert.Throws() Failure
value-not-reference#kill#BRIDGE#!ReferenceEquals(tenant, httpContext.RequestServices.GetRequiredService<TenantContext>())#tenant.AccountId != httpContext.RequestServices.GetRequiredService<TenantContext>().AccountId#McpCallContextTests.SecondScope_WithTheCallersIdentityCopiedIn_Throws#No exception was thrown
tenant-unchecked#kill#BRIDGE#if (!tenant.IsResolved)#if (false)#McpCallContextTests.TenantUnresolved_Throws#No exception was thrown
actor-unchecked#kill#BRIDGE#if (!user.IsResolved || user.UserId == Guid.Empty)#if (false)#McpCallContextTests.ActorUnresolved_Throws#Assert.Contains() Failure
actor-isresolved-only-holds#hold#BRIDGE#if (!user.IsResolved || user.UserId == Guid.Empty)#if (user.UserId == Guid.Empty)#McpCallContextTests.ActorUnresolved_Throws#
system-actor-admitted#kill#BRIDGE#if (!user.IsResolved || user.UserId == Guid.Empty)#if (!user.IsResolved)#McpCallContextTests.SystemActor_Throws#Assert.Contains() Failure
flock-scope-unchecked#kill#BRIDGE#if (!flockScope.IsResolved)#if (false)#McpCallContextTests.FlockScopeUnresolved_Throws#No exception was thrown
isunrestricted-wrong-fix#kill#BRIDGE#if (!flockScope.IsResolved)#if (!flockScope.IsUnrestricted)#McpCallContextTests.RestrictedWorkerThroughAConnectedApp_ExposesTheRequestIdentity#flock scope is unresolved
isunrestricted-inverted-fix#kill#BRIDGE#if (!flockScope.IsResolved)#if (flockScope.IsUnrestricted)#McpCallContextTests.ResolvedUnrestrictedScope_IsAccepted#flock scope is unresolved
connected-app-unchecked#kill#BRIDGE#?? throw Refused("the caller did not arrive through a connected app");#?? new ConnectedApp("", null);#McpCallContextTests.SessionJwtCaller_Throws#No exception was thrown
session-jwt-admitted#kill#BRIDGE#?? throw Refused("the caller did not arrive through a connected app");#?? new ConnectedApp("", null);#McpCallContextPipelineTests.SessionJwtCaller_IsRefused#Expected: Conflict
flock-restriction-dropped#kill#BRIDGE#IsFlockRestricted = !flockScope.IsUnrestricted;#IsFlockRestricted = false;#McpCallContextTests.RestrictedWorkerThroughAConnectedApp_ExposesTheRequestIdentity#Assert.True() Failure
bridge-unregistered#kill#IDENTITY#        services.AddScoped<McpCallContext>();\n##McpCallContextPipelineTests.OAuthCaller_ResolvesTheRequestsIdentity#Expected: OK
bridge-registered-by-factory#kill#IDENTITY#services.AddScoped<McpCallContext>();#services.AddScoped(sp => new McpCallContext(sp.GetRequiredService<IHttpContextAccessor>(), sp.GetRequiredService<TenantContext>(), sp.GetRequiredService<CurrentUserContext>(), sp.GetRequiredService<FlockScope>()));#McpSecondaryScopeTests.RealHost_NoToolOrContractReachesASecondaryScope#McpCallContext: registered through a factory delegate
request-reader-unchecked#kill#WALK#    private static bool ReadsTheRequest(Type type) => Reaches(RequestReaders, type);#    private static bool ReadsTheRequest(Type type) => false;#McpSecondaryScopeTests.HelperReadingRequestServices_IsAFinding#Assert.Single() Failure
request-reader-unchecked-real-host#kill#WALK#    private static bool ReadsTheRequest(Type type) => Reaches(RequestReaders, type);#    private static bool ReadsTheRequest(Type type) => false;#McpSecondaryScopeTests.RealHost_NoToolOrContractReachesASecondaryScope#stale review: McpCallContext reads the request
EOF
)

LOG_DIR=$(mktemp -d -t mcp-mutants.XXXXXX)
echo "logs: $LOG_DIR"

build() { dotnet build "$TESTS" >"$1" 2>&1; }

run_tests() { # filter name
  dotnet test "$TESTS" --no-build --filter "$1" \
    --logger "trx;LogFileName=result.trx" --results-directory "$LOG_DIR/$2" >"$LOG_DIR/$2.log" 2>&1
}

mutate() { # file find replace
  python3 - "$1" "$2" "$3" <<'PY'
import sys
path, find, replace = sys.argv[1], sys.argv[2].replace('\\n', '\n'), sys.argv[3].replace('\\n', '\n')
text = open(path, encoding='utf-8-sig').read()
if text.count(find) != 1:
    sys.exit(f"anchor found {text.count(find)} times in {path}")
open(path, 'w', encoding='utf-8').write(text.replace(find, replace))
PY
}

# verdict trx test expect declared -> one verdict line; test '*' summarises the whole run
verdict() {
  python3 - "$@" <<'PY'
import sys, xml.etree.ElementTree as ET
trx, test, expect, declared = sys.argv[1:5]
ns = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
try:
    root = ET.parse(trx).getroot()
except (OSError, ET.ParseError) as error:
    print(f"INCONCLUSIVE no readable TRX ({error})"); sys.exit()
results = list(root.iter(f'{ns}UnitTestResult'))
if test == '*':
    failed = [r.get('testName') for r in results if r.get('outcome') != 'Passed']
    print(f"{len(results)} ran, {len(failed)} not passed {failed[:3]}" if failed or not results else f"green, {len(results)} passed")
    sys.exit()
results = [r for r in results if r.get('testName') == test]
if len(results) != 1:
    print(f"INCONCLUSIVE {len(results)} results for {test}"); sys.exit()
outcome = results[0].get('outcome')
error = results[0].find(f'{ns}Output/{ns}ErrorInfo/{ns}Message')
message = ''.join(error.itertext()) if error is not None else ''
first = (message.strip().splitlines() or [''])[0][:160]
if outcome == 'Passed':
    print('held, the test still passes' if expect == 'hold' else 'SURVIVED, the test still passes')
elif outcome != 'Failed':
    print(f"INCONCLUSIVE, test outcome {outcome}")
elif expect == 'kill' and declared in message:
    print(f"killed: {first}")
else:
    print(f"WRONG, failed without '{declared}': {first}")
PY
}

FILES=("$BRIDGE" "$IDENTITY" "$WALK")
restore() { git checkout -- "${FILES[@]}"; }
if ! git diff --quiet -- "${FILES[@]}"; then
  echo "refusing: mutated files have uncommitted changes, and restore would discard them" >&2
  exit 2
fi
trap restore EXIT

suite() { # name -> 0 when green
  run_tests "$SUITE" "$1"
  local summary
  summary=$(verdict "$LOG_DIR/$1/result.trx" '*' '' '')
  echo "$1: $summary"
  [[ $summary == green* ]]
}

build "$LOG_DIR/baseline-build.log" || { echo "baseline: build failed" >&2; exit 1; }
suite baseline || { echo "baseline is not green, so no mutant can prove anything" >&2; exit 1; }

failures=0
while IFS='#' read -r name expect file_key find replace test declared; do
  [[ -z "$name" ]] && continue
  file=${!file_key}
  if ! mutate "$file" "$find" "$replace" 2>"$LOG_DIR/$name-mutate.log"; then
    result="INCONCLUSIVE, the mutant did not apply ($LOG_DIR/$name-mutate.log)"
  elif ! build "$LOG_DIR/$name-build.log"; then
    result="INCONCLUSIVE, the mutant does not compile ($LOG_DIR/$name-build.log)"
  else
    run_tests "FullyQualifiedName=$TEST_NS.$test" "$name"
    result=$(verdict "$LOG_DIR/$name/result.trx" "$TEST_NS.$test" "$expect" "$declared")
  fi
  echo "$name: $result"
  case "$expect:${result%%[,:]*}" in
    kill:killed | hold:held) ;;
    *) failures=$((failures + 1)) ;;
  esac
  restore
done <<<"$MUTANTS"

build "$LOG_DIR/restore-build.log" || { echo "restore: build failed" >&2; exit 1; }
suite restore || failures=$((failures + 1))
echo "failures: $failures"
exit $((failures > 0))
