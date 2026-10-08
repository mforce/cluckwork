#!/usr/bin/env bash
#
# tools/oauth/mutation-check.sh — proves each #795 OAuth claim has a test that fails
# when the claim breaks.
#
# Baseline green, then per mutant: apply one exact-string edit (it must match once),
# rebuild (a mutant that does not compile proves nothing, so it fails the run), and run
# the named tests. "kill" mutants must turn them red. "hold" mutants must leave them
# green, because a second wall still stands. Every file is restored after each mutant,
# and a final run must be green again.
#
# Usage:  sg docker -c 'bash tools/oauth/mutation-check.sh'   (Testcontainers needs Docker)

set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

IDENTITY=src/Cluckwork.Api/Hosting/CluckworkIdentityServiceCollectionExtensions.cs
SERVER=src/Cluckwork.Infrastructure/Modules/Access/OAuth/OAuthServerRegistration.cs
ENDPOINT=src/Cluckwork.Api/Modules/Access/OAuth/OAuthEndpoints.cs
TESTS=tests/Cluckwork.Api.IntegrationTests
ALL='FullyQualifiedName~OAuthServer'

# name # expect # file # find # replace # test filter  ('#' because C# anchors contain '|')
MUTANTS=$(cat <<'EOF'
production-gate#kill#IDENTITY#        if (role is not ProcessRole.Serving || environment.IsProduction()#        if (role is not ProcessRole.Serving#FullyQualifiedName~OAuthServerProductionTests
pkce-optional#kill#SERVER#                    .RequireProofKeyForCodeExchange()\n##FullyQualifiedName~AuthorizationRequestWithoutPkce_IsRefused
self-contained-tokens#kill#SERVER#                    .UseReferenceAccessTokens()\n##FullyQualifiedName~IssuesAReferenceTokenTheResourceSideAccepts
default-token-lifetime#kill#SERVER#                    .SetAccessTokenLifetime(null)\n##FullyQualifiedName~IssuesAReferenceTokenTheResourceSideAccepts
per-process-token-keys#kill#SERVER#                    .AddEphemeralSigningKey()\n                    .UseDataProtection();#                    .AddEphemeralSigningKey();#FullyQualifiedName~CodeAndToken_CrossReplicas_ThroughTheSharedKeyRing
openid-granted#kill#SERVER#                server.Configure(options => options.Scopes.Remove(Scopes.OpenId));\n##FullyQualifiedName~OpenIdScope_IsRefused
oauth-on-default-scheme#kill#IDENTITY#            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)#            .AddAuthentication("mutant").AddPolicyScheme("mutant", null, o => o.ForwardDefaultSelector = c => c.Request.Headers.Authorization.ToString().Count(ch => ch == '.') == 2 ? JwtBearerDefaults.AuthenticationScheme : "OpenIddict.Validation.AspNetCore")#FullyQualifiedName~OAuthToken_IsRejectedByBusinessEndpoints_AtAuthentication
session-claims-in-token#kill#ENDPOINT#    private static IResult Authorize(ICurrentUser currentUser)\n    {\n        if (!currentUser.IsResolved) return Results.Unauthorized();\n\n        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);\n        identity.SetClaim(Claims.Subject, currentUser.UserId.ToString());#    private static IResult Authorize(ICurrentUser currentUser, HttpContext http)\n    {\n        if (!currentUser.IsResolved) return Results.Unauthorized();\n\n        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);\n        identity.SetClaim(Claims.Subject, currentUser.UserId.ToString());\n        identity.SetClaim("account_id", http.User.FindFirst("account_id")!.Value);\n        identity.SetClaim("credential_epoch", http.User.FindFirst("credential_epoch")!.Value);#FullyQualifiedName~OAuthToken_ForcedThroughTheDefaultScheme_IsStillRejected
account-id-in-token#hold#ENDPOINT#    private static IResult Authorize(ICurrentUser currentUser)\n    {\n        if (!currentUser.IsResolved) return Results.Unauthorized();\n\n        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);\n        identity.SetClaim(Claims.Subject, currentUser.UserId.ToString());#    private static IResult Authorize(ICurrentUser currentUser, HttpContext http)\n    {\n        if (!currentUser.IsResolved) return Results.Unauthorized();\n\n        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);\n        identity.SetClaim(Claims.Subject, currentUser.UserId.ToString());\n        identity.SetClaim("account_id", http.User.FindFirst("account_id")!.Value);#FullyQualifiedName~OAuthToken_ForcedThroughTheDefaultScheme_IsStillRejected
EOF
)

LOG_DIR=$(mktemp -d -t oauth-mutants.XXXXXX)
echo "logs: $LOG_DIR"

run_tests() { # filter log
  dotnet test "$TESTS" --no-build --filter "$1" >"$2" 2>&1
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

restore() {
  git checkout -- "$IDENTITY" "$SERVER" "$ENDPOINT"
}

if ! git diff --quiet -- "$IDENTITY" "$SERVER" "$ENDPOINT"; then
  echo "refusing: mutated files have uncommitted changes, and restore would discard them" >&2
  exit 2
fi
trap restore EXIT

failures=0
build "$LOG_DIR/baseline-build.log" || { echo "BASELINE build failed" >&2; exit 1; }
if run_tests "$ALL" "$LOG_DIR/baseline.log"; then echo "baseline: green"; else
  echo "baseline: RED, so no mutant can prove anything (see $LOG_DIR/baseline.log)" >&2; exit 1; fi

while IFS='#' read -r name expect file_key find replace filter; do
  [[ -z "$name" ]] && continue
  file=${!file_key}
  if ! mutate "$file" "$find" "$replace"; then
    echo "$name: anchor missing, mutant not applied"; failures=$((failures + 1)); restore; continue
  fi
  if ! build "$LOG_DIR/$name-build.log"; then
    echo "$name: does not compile ($LOG_DIR/$name-build.log)"; failures=$((failures + 1)); restore; continue
  fi
  if run_tests "$filter" "$LOG_DIR/$name.log"; then outcome=green; else outcome=red; fi
  case "$expect:$outcome" in
    kill:red)    echo "$name: killed" ;;
    hold:green)  echo "$name: held (the second wall still refuses it)" ;;
    kill:green)  echo "$name: SURVIVED ($LOG_DIR/$name.log)"; failures=$((failures + 1)) ;;
    hold:red)    echo "$name: went red, but this wall was expected to hold ($LOG_DIR/$name.log)"; failures=$((failures + 1)) ;;
  esac
  restore
done <<<"$MUTANTS"

build "$LOG_DIR/restore-build.log" || { echo "RESTORE build failed" >&2; exit 1; }
if run_tests "$ALL" "$LOG_DIR/restore.log"; then echo "restore: green"; else
  echo "restore: RED ($LOG_DIR/restore.log)"; failures=$((failures + 1)); fi

echo "failures: $failures"
exit $((failures > 0))
