#!/usr/bin/env bash
#
# tools/oauth/mutation-check.sh — proves each #795 and #796 OAuth claim has a test that
# fails when the claim breaks.
#
# Baseline green, then per mutant: apply one exact-string edit (it must match once),
# rebuild, run the ONE named test with a TRX logger, and classify from the TRX, never
# from the exit code. A failing Docker container, an aborted test host or a test that
# never ran also exit nonzero; counting those as kills would credit a test with
# catching a mutant it never saw. The verdicts:
#
#   killed        the named test failed and its message carries the declared text
#   held          a "hold" mutant's test still passed
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
LIMITS=src/Cluckwork.Api/Hosting/CluckworkRateLimitingServiceCollectionExtensions.cs
VERIFIER=src/Cluckwork.Infrastructure/Modules/Access/Identity/CredentialEpochVerifier.cs
EPOCH=src/Cluckwork.Api/Middleware/CredentialEpochMiddleware.cs
MUST_CHANGE=src/Cluckwork.Api/Middleware/MustChangePasswordMiddleware.cs
TESTS=tests/Cluckwork.Api.IntegrationTests
TEST_NS=Cluckwork.Api.IntegrationTests
SUITE='FullyQualifiedName~OAuth'
SUITE_MIN=27

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
oauth-on-default-scheme#kill#IDENTITY#                        OAuthEndpoints.AcceptsOAuthTokens(context.GetEndpoint())\n#                        OAuthEndpoints.AcceptsOAuthTokens(context.GetEndpoint()) || context.Request.Headers.Authorization.ToString().Count(ch => ch == '.') != 2\n#OAuthServerTests.OAuthToken_IsRejectedByBusinessEndpoints_AtAuthentication#Expected: Unauthorized
jwt-by-token-shape#kill#IDENTITY#                        OAuthEndpoints.AcceptsOAuthTokens(context.GetEndpoint())\n#                        OAuthEndpoints.AcceptsOAuthTokens(context.GetEndpoint()) && context.Request.Headers.Authorization.ToString().Count(ch => ch == '.') != 2\n#OAuthFailClosedTests.SessionJwt_IsRefused_WhereOAuthTokensAreAccepted#Expected: Unauthorized
authenticate-at-authorization#kill#ENDPOINT#            .WithMetadata(new AcceptsOAuthTokensMarker())\n            .RequireRateLimiting(RateLimitingOptions.OAuthApiPolicyName)\n            .RequireAuthorization(policy => policy.RequireAssertion(#            .RequireRateLimiting(RateLimitingOptions.OAuthApiPolicyName)\n            .RequireAuthorization(policy => policy.AddAuthenticationSchemes("OpenIddict.Validation.AspNetCore").RequireAssertion(#OAuthFailClosedTests.DisabledUser_IsRefused_OnTheNextRequest#Expected: Unauthorized
authenticate-at-authorization-flocks#kill#ENDPOINT#            .WithMetadata(new AcceptsOAuthTokensMarker())\n            .RequireRateLimiting(RateLimitingOptions.OAuthApiPolicyName)\n            .RequireAuthorization(policy => policy.RequireAssertion(#            .RequireRateLimiting(RateLimitingOptions.OAuthApiPolicyName)\n            .RequireAuthorization(policy => policy.AddAuthenticationSchemes("OpenIddict.Validation.AspNetCore").RequireAssertion(#OAuthFailClosedTests.Worker_IsFlockScoped#the worker's OAuth caller is unrestricted
role-claim-dropped#kill#ENDPOINT#"credential_epoch", Claims.Role, #"credential_epoch", #OAuthFailClosedTests.OAuthToken_CarriesTheSessionPrincipal_ThroughTheWholeChain#Collections differ
disabled-unchecked#kill#VERIFIER#        if (credentialState.DisabledAt is not null)\n            return CredentialVerdict.Disabled;\n##OAuthFailClosedTests.DisabledUser_IsRefused_OnTheNextRequest#Expected: Unauthorized
suspended-unchecked#kill#VERIFIER#        if (credentialState.AccountIsActive != true)\n            return CredentialVerdict.FarmSuspended;\n##OAuthFailClosedTests.SuspendedFarm_IsRefused_OnTheNextRequest#Expected: Unauthorized
epoch-exempts-oauth#kill#EPOCH#            && !IsLogoutPath(context.Request.Path))#            && !IsLogoutPath(context.Request.Path) && context.User.FindFirst("oi_au_id") is null)#OAuthFailClosedTests.RoleChange_RevokesTheToken#Expected: Unauthorized
epoch-exempts-oauth-suspended#kill#EPOCH#            && !IsLogoutPath(context.Request.Path))#            && !IsLogoutPath(context.Request.Path) && context.User.FindFirst("oi_au_id") is null)#OAuthFailClosedTests.SuspendedFarm_IsRefused_OnTheNextRequest#Expected: Unauthorized
must-change-may-authorize#kill#MUST_CHANGE#        "/api/v1/auth/logout",\n#        "/api/v1/auth/logout",\n        "/api/v1/oauth/authorize",\n#OAuthFailClosedTests.MustChangePassword_BlocksIssuance#Expected: Forbidden
scope-gate-removed#kill#ENDPOINT#\n            .RequireAuthorization(policy => policy.RequireAssertion(context =>\n                scopes.Any(context.User.HasScope)));#;#OAuthFailClosedTests.ScopeAndRole_AreBothRequired#Expected: Forbidden
authorization-unchecked#kill#SERVER#                validation.EnableAuthorizationEntryValidation();\n##OAuthFailClosedTests.Disconnect_RefusesTheAccessToken_OnTheNextRequest#Expected: Unauthorized
unbound-token-trusted#kill#SERVER#                        if (string.IsNullOrEmpty(context.AuthorizationId))\n                            context.Reject(Errors.InvalidToken, "The token is not bound to an authorization.");\n##OAuthFailClosedTests.TokenWithoutAnAuthorization_IsRefused#Expected: Unauthorized
refresh-grant-allowed#kill#SERVER#                    .AllowAuthorizationCodeFlow()#                    .AllowAuthorizationCodeFlow().AllowRefreshTokenFlow()#OAuthFailClosedTests.Disconnect_LeavesNoWayToANewToken#unsupported_grant_type
query-string-token#kill#SERVER#                    .DisableAccessTokenExtractionFromBodyForm()\n                    .DisableAccessTokenExtractionFromQueryString();#                    .DisableAccessTokenExtractionFromBodyForm();#OAuthFailClosedTests.TokenInTheQueryString_IsIgnored#Expected: Unauthorized
api-limit-removed#kill#ENDPOINT#            .RequireRateLimiting(RateLimitingOptions.OAuthApiPolicyName)\n##OAuthFailClosedTests.OAuthApiCalls_AreRateLimited_PerToken#Collections differ
api-limit-per-ip#kill#LIMITS#,\n                    RateLimitKey.ForBearer)#)#OAuthFailClosedTests.OAuthApiCalls_AreRateLimited_PerToken#Expected: OK
token-limit-removed#kill#ENDPOINT#            .RequireRateLimiting(RateLimitingOptions.OAuthTokenPolicyName)\n##OAuthFailClosedTests.TokenEndpoint_IsRateLimited#Expected: TooManyRequests
authorize-limit-removed#kill#ENDPOINT#            .RequireRateLimiting(RateLimitingOptions.OAuthAuthorizePolicyName)\n##OAuthFailClosedTests.AuthorizeEndpoint_IsRateLimited#Expected: TooManyRequests
ambient-bearer-honoured#kill#ENDPOINT#new IgnoresAmbientPrincipalAttribute(), new ReadsRequestBodyAttribute()#new ReadsRequestBodyAttribute()#OAuthFailClosedTests.TokenEndpoint_IgnoresAnAmbientSessionBearer#Expected: OK
oauth-token-local-limiter#kill#LIMITS#            limiter.AddPolicy<string>(\n                RateLimitingOptions.OAuthTokenPolicyName,\n                new DistributedFixedWindowPolicy(\n                    RateLimitingOptions.OAuthTokenPolicyName,\n                    rateLimiting.OAuthToken.PermitLimit,\n                    TimeSpan.FromSeconds(rateLimiting.OAuthToken.WindowSeconds)))#            limiter.AddFixedWindowLimiter(RateLimitingOptions.OAuthTokenPolicyName, o => { o.PermitLimit = rateLimiting.OAuthToken.PermitLimit; o.Window = TimeSpan.FromSeconds(rateLimiting.OAuthToken.WindowSeconds); });#OAuthFailClosedTests.OAuthLimits_AreDecidedByTheSharedCounter#oauth-token did not ask the shared counter
oauth-authorize-local-limiter#kill#LIMITS#            limiter.AddPolicy<string>(\n                RateLimitingOptions.OAuthAuthorizePolicyName,\n                new DistributedFixedWindowPolicy(\n                    RateLimitingOptions.OAuthAuthorizePolicyName,\n                    rateLimiting.OAuthAuthorize.PermitLimit,\n                    TimeSpan.FromSeconds(rateLimiting.OAuthAuthorize.WindowSeconds)))#            limiter.AddFixedWindowLimiter(RateLimitingOptions.OAuthAuthorizePolicyName, o => { o.PermitLimit = rateLimiting.OAuthAuthorize.PermitLimit; o.Window = TimeSpan.FromSeconds(rateLimiting.OAuthAuthorize.WindowSeconds); });#OAuthFailClosedTests.OAuthLimits_AreDecidedByTheSharedCounter#oauth-authorize did not ask the shared counter
oauth-api-local-limiter#kill#LIMITS#            limiter.AddPolicy<string>(\n                RateLimitingOptions.OAuthApiPolicyName,\n                new DistributedFixedWindowPolicy(\n                    RateLimitingOptions.OAuthApiPolicyName,\n                    rateLimiting.OAuthApi.PermitLimit,\n                    TimeSpan.FromSeconds(rateLimiting.OAuthApi.WindowSeconds),\n                    RateLimitKey.ForBearer));#            limiter.AddFixedWindowLimiter(RateLimitingOptions.OAuthApiPolicyName, o => { o.PermitLimit = rateLimiting.OAuthApi.PermitLimit; o.Window = TimeSpan.FromSeconds(rateLimiting.OAuthApi.WindowSeconds); });#OAuthFailClosedTests.OAuthLimits_AreDecidedByTheSharedCounter#oauth-api did not ask the shared counter
post-authorize-allowed#kill#SERVER#                        if (!HttpMethods.IsGet(context.Transaction.GetHttpRequest()!.Method))\n                            context.Reject(Errors.InvalidRequest, "Authorization requests must use GET.");\n##OAuthFailClosedTests.AuthorizationPost_IsRefusedBeforeOpenIddictReadsIt#Sub-string not found
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
results = [r for r in root.iter(f'{ns}UnitTestResult') if r.get('testName') == test]
if len(results) != 1:
    print(f"INCONCLUSIVE expected one result for {test}, found {len(results)}"); sys.exit()
outcome = results[0].get('outcome')
error = results[0].find(f'{ns}Output/{ns}ErrorInfo/{ns}Message')
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

FILES=("$IDENTITY" "$TELEMETRY" "$SERVER" "$ENDPOINT" "$LIMITS" "$VERIFIER" "$EPOCH" "$MUST_CHANGE")
restore() { git checkout -- "${FILES[@]}"; }

if ! git diff --quiet -- "${FILES[@]}"; then
  echo "refusing: mutated files have uncommitted changes, and restore would discard them" >&2
  exit 2
fi
trap restore EXIT

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
