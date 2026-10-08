#!/usr/bin/env bash
#
# tools/oauth/mutation-check.sh — proves each #795 and #797 OAuth claim has a test that
# fails when the claim breaks.
#
# Baseline green, then per mutant: apply one exact-string edit (it must match once),
# rebuild, run the ONE named test with a TRX logger, and classify from the TRX, never
# from the exit code. A failing Docker container, an aborted test host or a test that
# never ran also exit nonzero; counting those as kills would credit a test with
# catching a mutant it never saw. The verdicts:
#
#   killed        the named test failed and its message carries the declared text
#   held          a "hold" mutant's test still passed, so the second wall stands
#   SURVIVED      a "kill" mutant's test passed
#   WRONG         the named test failed an assertion, but not the declared one
#   INCONCLUSIVE  no TRX, the test did not run, the run aborted, the mutant did not
#                 apply or compile, or the test died on an exception, not an assertion
#
# Anything but killed or held fails the run. Every file is restored after each mutant,
# and a final run must be green again.
#
# Usage:  sg docker -c 'bash tools/oauth/mutation-check.sh'   (Testcontainers needs Docker)

set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

IDENTITY=src/Cluckwork.Api/Hosting/CluckworkIdentityServiceCollectionExtensions.cs
TELEMETRY=src/Cluckwork.Api/Hosting/CluckworkTelemetryServiceCollectionExtensions.cs
SERVER=src/Cluckwork.Infrastructure/Modules/Access/OAuth/OAuthServerRegistration.cs
ENDPOINT=src/Cluckwork.Api/Modules/Access/OAuth/OAuthEndpoints.cs
REGISTRATION=src/Cluckwork.Api/Modules/Access/OAuth/ClientRegistration.cs
LIMITS=src/Cluckwork.Api/Hosting/CluckworkRateLimitingServiceCollectionExtensions.cs
PURGE=src/Cluckwork.Infrastructure/Modules/Access/OAuth/OAuthPurge.cs
SWEEP=src/Cluckwork.Infrastructure/Jobs/OAuthPurgeSweep.cs
WORKER=src/Cluckwork.Infrastructure/Jobs/DurableJobWorker.cs
TESTS=tests/Cluckwork.Api.IntegrationTests
TEST_NS=Cluckwork.Api.IntegrationTests
SUITE='FullyQualifiedName~OAuth'
SUITE_MIN=40

# name # expect # file # find # replace # test # declared failure text
# ('#' because C# anchors contain '|'; '\n' in a find or replace is a newline)
MUTANTS=$(cat <<'EOF'
production-gate#kill#IDENTITY#        if (role is not ProcessRole.Serving || environment.IsProduction()#        if (role is not ProcessRole.Serving#OAuthServerProductionTests.Production_RunsNoAuthorizationServer#Expected: NotFound
pkce-optional#kill#SERVER#                    .RequireProofKeyForCodeExchange()\n##OAuthServerTests.AuthorizationRequestWithoutPkce_IsRefused#Expected: BadRequest
plain-pkce-allowed#kill#SERVER#                    options.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain);\n##OAuthServerTests.PlainCodeChallenge_IsRefused#Expected: BadRequest
self-contained-tokens#kill#SERVER#                    .UseReferenceAccessTokens()\n##OAuthServerTests.AuthorizationCodeWithPkce_IssuesAReferenceTokenTheResourceSideAccepts#the access token has no ReferenceId
default-token-lifetime#kill#SERVER#                    .SetAccessTokenLifetime(null)\n##OAuthServerTests.AuthorizationCodeWithPkce_IssuesAReferenceTokenTheResourceSideAccepts#the access token carries expires_in
per-process-token-keys#kill#SERVER#                    .AddEphemeralSigningKey()\n                    .UseDataProtection();#                    .AddEphemeralSigningKey();#OAuthServerTests.CodeAndToken_CrossReplicas_ThroughTheSharedKeyRing#the second replica refused the first replica's code
openid-granted#kill#SERVER#                    options.Scopes.Remove(Scopes.OpenId);\n##OAuthServerTests.OpenIdScope_IsRefused#Expected: BadRequest
verifier-logged#kill#TELEMETRY#                    .Filter.ByExcluding(OpenIddictBelowWarning),#                    ,#OAuthServerTests.ProtocolSecrets_NeverReachTheLog#protocol secrets reached the log
parent-override-only#kill#TELEMETRY#                    .Filter.ByExcluding(OpenIddictBelowWarning),#                    .MinimumLevel.Override("OpenIddict", LogEventLevel.Warning),#OAuthServerTests.ProtocolSecrets_NeverReachTheLog#protocol secrets reached the log
oauth-on-default-scheme#kill#IDENTITY#            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)#            .AddAuthentication("mutant").AddPolicyScheme("mutant", null, o => o.ForwardDefaultSelector = c => c.Request.Headers.Authorization.ToString().Count(ch => ch == '.') == 2 ? JwtBearerDefaults.AuthenticationScheme : "OpenIddict.Validation.AspNetCore")#OAuthServerTests.OAuthToken_IsRejectedByBusinessEndpoints_AtAuthentication#Assert.Contains() Failure: Filter not matched
session-claims-in-token#kill#ENDPOINT#@SESSION_FIND@#@SESSION_REPLACE@#OAuthServerTests.OAuthToken_ForcedThroughTheDefaultScheme_IsStillRejected#Expected: Unauthorized
account-id-in-token#hold#ENDPOINT#@ACCOUNT_FIND@#@ACCOUNT_REPLACE@#OAuthServerTests.OAuthToken_ForcedThroughTheDefaultScheme_IsStillRejected#
redirect-any-http-host#kill#REGISTRATION#            || uri.Scheme == Uri.UriSchemeHttp && uri.Host is "127.0.0.1" or "[::1]");#            || uri.Scheme == Uri.UriSchemeHttp);#OAuthClientRegistrationTests.WiderRedirectUri_IsRefused#Expected: BadRequest
redirect-fragment#kill#REGISTRATION#        && uri.Fragment.Length == 0\n##OAuthClientRegistrationTests.WiderRedirectUri_IsRefused#Expected: BadRequest
grant-any#kill#REGISTRATION#                || grants.Any(grant => grant is not (GrantTypes.AuthorizationCode or GrantTypes.RefreshToken))))#))#OAuthClientRegistrationTests.WiderGrant_IsRefused#Expected: BadRequest
auth-method-any#kill#REGISTRATION#request.TokenEndpointAuthMethod is not (null or ClientAuthenticationMethods.None)#request.TokenEndpointAuthMethod is ""#OAuthClientRegistrationTests.WiderGrant_IsRefused#Expected: BadRequest
name-keeps-bidi#kill#REGISTRATION#                or UnicodeCategory.Format or UnicodeCategory.PrivateUse#                or UnicodeCategory.PrivateUse#OAuthClientRegistrationTests.ClientName_IsSanitized#Strings differ
name-uncapped#kill#REGISTRATION#elements.MoveNext() && capped.Length + elements.GetTextElement().Length <= MaxNameLength#elements.MoveNext()#OAuthClientRegistrationTests.ClientName_IsSanitized#Strings differ
discovery-silent#kill#SERVER#                        context.Metadata["registration_endpoint"] =\n                            new Uri(context.AuthorizationEndpoint!, "register").AbsoluteUri;\n##OAuthClientRegistrationTests.Discovery_AdvertisesTheRegistrationEndpoint#discovery does not advertise registration_endpoint
register-unlimited#kill#ENDPOINT#            .RequireRateLimiting(RateLimitingOptions.OAuthRegisterPolicyName)\n##OAuthClientRegistrationTests.Registration_IsRateLimitedPerClientIp_OnTheSharedCounter#Collections differ
register-process-local#kill#LIMITS#            limiter.AddPolicy<string>(\n                RateLimitingOptions.OAuthRegisterPolicyName,\n                new DistributedIpFixedWindowPolicy(\n                    RateLimitingOptions.OAuthRegisterPolicyName,\n                    rateLimiting.OAuthRegister.PermitLimit,\n                    TimeSpan.FromSeconds(rateLimiting.OAuthRegister.WindowSeconds)));#            limiter.AddPolicy(RateLimitingOptions.OAuthRegisterPolicyName, context => RateLimitPartition.GetFixedWindowLimiter(RateLimitKey.ForClient(context.Connection.RemoteIpAddress), _ => new FixedWindowRateLimiterOptions { PermitLimit = rateLimiting.OAuthRegister.PermitLimit, Window = TimeSpan.FromSeconds(rateLimiting.OAuthRegister.WindowSeconds) }));#OAuthClientRegistrationTests.Registration_IsRateLimitedPerClientIp_OnTheSharedCounter#the registration limit did not count on the shared counter
register-reads-bearer#kill#ENDPOINT#            .WithMetadata(new IgnoresAmbientPrincipalAttribute())\n##OAuthClientRegistrationTests.Registration_IgnoresASessionBearer#Expected: Created
tokens-unpruned#kill#PURGE#tokens.PruneAsync(pruneBefore, ct)#tokens.PruneAsync(DateTimeOffset.MinValue, ct)#OAuthPurgeTests.DeadRowsPastRetention_ArePruned_AndALiveConnectionSurvives#Collections differ
authorizations-unpruned#kill#PURGE#authorizations.PruneAsync(pruneBefore, ct)#authorizations.PruneAsync(DateTimeOffset.MinValue, ct)#OAuthPurgeTests.DeadRowsPastRetention_ArePruned_AndALiveConnectionSurvives#Collection was not empty
retention-ignored#kill#PURGE#tokens.PruneAsync(pruneBefore, ct)#tokens.PruneAsync(DateTimeOffset.UtcNow, ct)#OAuthPurgeTests.DeadRowsInsideRetention_AreKept#a revoked token was pruned inside the retention
approved-app-deleted#kill#PURGE#\n                && !application.Authorizations.Any()\n                && !application.Tokens.Any())#)#OAuthPurgeTests.DeadRowsPastRetention_ArePruned_AndALiveConnectionSurvives#the purge threw
unapproved-kept#kill#PURGE#            .ExecuteDeleteAsync(ct);#            .CountAsync(ct);#OAuthPurgeTests.UnapprovedApplication_IsDeletedOnlyAfterTheWindow#an unapproved application outlived its window
window-ignored#kill#PURGE#EF.Property<DateTimeOffset>(application, OAuthApplicationConfiguration.CreatedAtUtc) < unapprovedBefore\n                && ##OAuthPurgeTests.UnapprovedApplication_IsDeletedOnlyAfterTheWindow#an unapproved application was deleted inside its window
sweep-window-dropped#kill#SWEEP#now - UnapprovedWindow, ct)#now, ct)#OAuthPurgeTests.Sweep_RunsOnlyOnTheLeader#the sweep deleted an application inside its window
follower-sweeps#kill#WORKER#            if (leadership == LeaseStatus.Follower)\n            {\n#            if (leadership == LeaseStatus.Follower)\n            {\n                if (oauthPurgeSweep is not null) await oauthPurgeSweep.RunAsync(stoppingToken);\n#OAuthPurgeTests.Sweep_RunsOnlyOnTheLeader#a follower ran the OAuth sweep
sweep-unwired#kill#WORKER#        if (oauthPurgeSweep is not null)\n            await oauthPurgeSweep.RunAsync(ct);\n##OAuthPurgeTests.Sweep_RunsOnlyOnTheLeader#the leader did not run the OAuth sweep
EOF
)

LOG_DIR=$(mktemp -d -t oauth-mutants.XXXXXX)
echo "logs: $LOG_DIR"

run_tests() { # filter name -> TRX at $LOG_DIR/<name>/result.trx
  dotnet test "$TESTS" --no-build --filter "$1" \
    --logger "trx;LogFileName=result.trx" --results-directory "$LOG_DIR/$2" >"$LOG_DIR/$2.log" 2>&1
}

build() { # log
  dotnet build "$TESTS" >"$1" 2>&1
}

mutate() { # file find replace -> 0 when applied exactly once
  python3 - "$1" "$2" "$3" <<'PY'
import sys
path, find, replace = sys.argv[1], sys.argv[2].replace('\\n', '\n'), sys.argv[3].replace('\\n', '\n')
text = open(path, encoding='utf-8-sig').read()
if text.count(find) != 1:
    sys.exit(f"anchor found {text.count(find)} times in {path}")
open(path, 'w', encoding='utf-8').write(text.replace(find, replace))
PY
}

# Prints a verdict word for the named test in a TRX, then the reason.
verdict() { # trx test expect declared
  python3 - "$@" <<'PY'
import re, sys, xml.etree.ElementTree as ET
trx, test, expect, declared = sys.argv[1:5]
ns = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
try:
    root = ET.parse(trx).getroot()
except (OSError, ET.ParseError) as error:
    print(f"INCONCLUSIVE no readable TRX ({error})"); sys.exit()
summary = root.find(f'{ns}ResultSummary')
if summary is None or summary.get('outcome') in ('Aborted', 'Error', 'Timeout'):
    print(f"INCONCLUSIVE run outcome {None if summary is None else summary.get('outcome')}"); sys.exit()
# A theory reports one result per case, named test(args); the mutant counts as caught
# when any case fails, and that case's message is the one judged.
results = [r for r in root.iter(f'{ns}UnitTestResult')
           if r.get('testName') == test or r.get('testName', '').startswith(test + '(')]
if not results:
    print(f"INCONCLUSIVE no result for {test}"); sys.exit()
if any(r.get('testName') == test for r in results) and len(results) != 1:
    print(f"INCONCLUSIVE expected one result for {test}, found {len(results)}"); sys.exit()
failed = [r for r in results if r.get('outcome') == 'Failed']
result = failed[0] if failed else next((r for r in results if r.get('outcome') != 'Passed'), results[0])
outcome = result.get('outcome')
error = result.find(f'{ns}Output/{ns}ErrorInfo/{ns}Message')
message = ''.join(error.itertext()) if error is not None else ''
first = (message.strip().splitlines() or [''])[0][:160]
if outcome == 'Passed':
    print('held, the test still passes' if expect == 'hold' else 'SURVIVED, the test still passes')
elif outcome != 'Failed':
    print(f"INCONCLUSIVE, test outcome {outcome}")
elif declared and declared in message:
    print(f"killed: {first}")
elif re.match(r'\s*[\w.`]+Exception\s*:', message) and not message.lstrip().startswith('Xunit.Sdk.'):
    print(f"INCONCLUSIVE, died on an exception, not an assertion: {first}")
elif expect == 'hold':
    print(f"WRONG, a hold mutant went red: {first}")
else:
    print(f"WRONG, failed without '{declared}': {first}")
PY
}

# Baseline and restore must show a clean test process, a completed TRX run and every
# test passed; a run that errored after reporting its results is not green.
suite_green() { # name -> 0 when green
  run_tests "$SUITE" "$1"
  python3 - "$LOG_DIR/$1/result.trx" "$SUITE_MIN" "$?" <<'PY'
import sys, xml.etree.ElementTree as ET
trx, minimum, status = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
ns = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
try:
    summary = ET.parse(trx).getroot().find(f'{ns}ResultSummary')
except (OSError, ET.ParseError):
    print('no readable TRX'); sys.exit(1)
counters = summary.find(f'{ns}Counters').attrib
total, executed, passed = (int(counters[k]) for k in ('total', 'executed', 'passed'))
problems = [f'test process exited {status}'] if status else []
if summary.get('outcome') != 'Completed':
    problems.append(f"run outcome {summary.get('outcome')}")
if summary.find(f'{ns}RunInfos') is not None:
    problems.append('run-level errors recorded')
if total < minimum or executed != total or passed != total:
    problems.append(f'{passed} passed, {executed} executed, {total} total, {minimum} required')
print('; '.join(problems) or f'{passed}/{total} passed')
sys.exit(1 if problems else 0)
PY
}

FILES=("$IDENTITY" "$TELEMETRY" "$SERVER" "$ENDPOINT" "$REGISTRATION" "$LIMITS" "$PURGE" "$SWEEP" "$WORKER")
restore() { git checkout -- "${FILES[@]}"; }

if ! git diff --quiet -- "${FILES[@]}"; then
  echo "refusing: mutated files have uncommitted changes, and restore would discard them" >&2
  exit 2
fi
trap restore EXIT

# The endpoint's two mutants rewrite a multi-line block; kept out of the table above
# so each row stays readable.
AUTHORIZE='    private static IResult Authorize(ICurrentUser currentUser)\n    {\n        if (!currentUser.IsResolved) return Results.Unauthorized();\n\n        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);\n        identity.SetClaim(Claims.Subject, currentUser.UserId.ToString());'
WITH_CONTEXT=${AUTHORIZE/'Authorize(ICurrentUser currentUser)'/'Authorize(ICurrentUser currentUser, HttpContext http)'}
ACCOUNT_ID='\n        identity.SetClaim("account_id", http.User.FindFirst("account_id")!.Value);'
EPOCH='\n        identity.SetClaim("credential_epoch", http.User.FindFirst("credential_epoch")!.Value);'
MUTANTS=${MUTANTS//@SESSION_FIND@/$AUTHORIZE}
MUTANTS=${MUTANTS//@SESSION_REPLACE@/$WITH_CONTEXT$ACCOUNT_ID$EPOCH}
MUTANTS=${MUTANTS//@ACCOUNT_FIND@/$AUTHORIZE}
MUTANTS=${MUTANTS//@ACCOUNT_REPLACE@/$WITH_CONTEXT$ACCOUNT_ID}

failures=0
build "$LOG_DIR/baseline-build.log" || { echo "baseline: build failed" >&2; exit 1; }
if summary=$(suite_green baseline); then echo "baseline: green, $summary"; else
  echo "baseline: NOT GREEN ($summary), so no mutant can prove anything ($LOG_DIR/baseline.log)" >&2; exit 1; fi

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
if summary=$(suite_green restore); then echo "restore: green, $summary"; else
  echo "restore: NOT GREEN ($summary, $LOG_DIR/restore.log)"; failures=$((failures + 1)); fi

echo "failures: $failures"
exit $((failures > 0))
