#!/usr/bin/env bash
#
# tools/oauth/mutation-check.sh — proves each #795 to #799 and #1148 OAuth claim has a test that
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
REGISTRATION=src/Cluckwork.Infrastructure/OAuth/ClientMetadata.cs
FETCHER=src/Cluckwork.Infrastructure/OAuth/ClientMetadataFetcher.cs
DOCUMENTS=src/Cluckwork.Infrastructure/OAuth/ClientMetadataDocuments.cs
IDEMPOTENCY=src/Cluckwork.Api/Middleware/IdempotencyMiddleware.cs
LIMITS=src/Cluckwork.Api/Hosting/CluckworkRateLimitingServiceCollectionExtensions.cs
PURGE=src/Cluckwork.Infrastructure/Modules/Access/OAuth/OAuthPurge.cs
SWEEP=src/Cluckwork.Infrastructure/Jobs/OAuthPurgeSweep.cs
WORKER=src/Cluckwork.Infrastructure/Jobs/DurableJobWorker.cs
STAMP_MIGRATION=src/Cluckwork.Infrastructure/Persistence/Migrations/20261008055839_AddOAuthApplicationCreatedAt.cs
STAMP_CONFIG=src/Cluckwork.Infrastructure/Modules/Access/OAuth/OAuthApplicationConfiguration.cs
VERIFIER=src/Cluckwork.Infrastructure/Modules/Access/Identity/CredentialEpochVerifier.cs
EPOCH=src/Cluckwork.Api/Middleware/CredentialEpochMiddleware.cs
MUST_CHANGE=src/Cluckwork.Api/Middleware/MustChangePasswordMiddleware.cs
CONNECTED=src/Cluckwork.Infrastructure/Modules/Access/OAuth/ConnectedApps.cs
LAST_USED=src/Cluckwork.Infrastructure/Modules/Access/OAuth/OAuthLastUsedStamp.cs
ME=src/Cluckwork.Api/Modules/Access/Me/MeEndpoints.cs
PROGRAM=src/Cluckwork.Api/Program.cs
ACCOUNT=src/Cluckwork.Domain/Modules/Farm/Accounts/Account.cs
ACCOUNT_ENDPOINTS=src/Cluckwork.Api/Modules/Farm/Accounts/AccountEndpoints.cs
SWITCH_HANDLER=src/Cluckwork.Application/Modules/Farm/Accounts/SetConnectedApps/SetConnectedAppsHandler.cs
SWITCH_MIGRATION=src/Cluckwork.Infrastructure/Persistence/Migrations/20261009193717_AddAccountAllowConnectedApps.cs
TESTS=tests/Cluckwork.Api.IntegrationTests
TEST_NS=Cluckwork.Api.IntegrationTests
SUITE='FullyQualifiedName~OAuth|FullyQualifiedName~ClientMetadata'
SUITE_MIN=263

# name # expect # file # find # replace # test # declared failure text
# ('#' because C# anchors contain '|'; '\n' in a find or replace is a newline)
MUTANTS=$(cat <<'EOF'
production-issuer-optional#kill#IDENTITY#        if (environment.IsProduction()) return EnsureOAuthIssuer(issuer);\n##OAuthServerProductionTests.Production_WithoutAnHttpsIssuer_RefusesToStart#No exception was thrown
production-http-issuer#kill#IDENTITY# && uri.Scheme == Uri.UriSchemeHttps##OAuthServerProductionTests.Production_WithoutAnHttpsIssuer_RefusesToStart#No exception was thrown
production-issuer-query#kill#IDENTITY#\n            && uri.Query.Length == 0 && uri.Fragment.Length == 0##OAuthServerProductionTests.Production_WithoutAnHttpsIssuer_RefusesToStart#No exception was thrown
scope-permissions-enforced#kill#SERVER#                    .IgnoreScopePermissions()\n##OAuthServerProductionTests.Production_ServesTheConnectFlow#Expected: OK
reconnect-without-password#kill#ENDPOINT#        if (stepUpToken is null)\n            return Results.Json(new ConsentRequest(\n                request.ClientId!,\n                await applications.GetDisplayNameAsync(application, ct),\n                ClientMetadata.VerifiedDomain(request.ClientId),\n                new Uri(ValidatedRedirectUri(context)).Host,\n                scopes,\n                [.. scopes.Where(allowed.Contains)],\n                AlreadyApproved: covering is not null,\n                AssignedFlocks: flockScope.IsUnrestricted ? null\n                    : [.. (await access.ListFlockAssignmentsAsync(currentUser.UserId, ct))\n                        .Select(assignment => assignment.FlockName).OfType<string>()]));\n\n        var proof = await access.ConsumeStepUpGrantAsync(tenant.AccountId, currentUser.UserId, stepUpToken, ct);\n        if (proof.IsFailure)\n            return Results.Problem(proof.Error.Description,\n                statusCode: StatusCodes.Status403Forbidden, title: proof.Error.Code);\n#        if (covering is null)\n        {\n            if (stepUpToken is null)\n                return Results.Json(new ConsentRequest(request.ClientId!, null, null, "", scopes, [], false, null));\n            var proof = await access.ConsumeStepUpGrantAsync(tenant.AccountId, currentUser.UserId, stepUpToken, ct);\n            if (proof.IsFailure)\n                return Results.Problem(proof.Error.Description,\n                    statusCode: StatusCodes.Status403Forbidden, title: proof.Error.Code);\n        }\n#OAuthConsentTests.ApprovedApp_AsksOnlyForThePassword#a session bearer alone minted a code
reconnect-grant-unspent#kill#ENDPOINT#        var proof = await access.ConsumeStepUpGrantAsync(#        var proof = covering is not null ? Cluckwork.Domain.Common.Result.Success() : await access.ConsumeStepUpGrantAsync(#OAuthConsentTests.Reconnect_SpendsTheGrant#Expected: Forbidden
redirect-host-unvalidated#kill#ENDPOINT#new Uri(ValidatedRedirectUri(context)).Host#new Uri(request.RedirectUri!).Host#OAuthConsentTests.OmittedRedirectUri_UsesTheRegisteredOne#Expected: OK
session-lifetime-unchecked#kill#IDENTITY#                    ValidateLifetime = true,#                    ValidateLifetime = false,#OAuthConsentTests.ExpiredSession_IsUnauthorized_NotARedirect#Expected: Unauthorized
grant-unchecked#kill#ENDPOINT#        if (proof.IsFailure)#        if (proof.IsFailure && stepUpToken.Length < 0)#OAuthConsentTests.Approval_WithoutAValidGrant_IsRefused#Expected: Forbidden
ad-hoc-authorization#kill#ENDPOINT#                identity, subject, applicationId, AuthorizationTypes.Permanent, scopes, token);#                identity, subject, applicationId, AuthorizationTypes.AdHoc, scopes, token);#OAuthConsentTests.ApprovedApp_AsksOnlyForThePassword#an approved app was shown its permissions again
wider-request-skipped#kill#ENDPOINT#            if (covering is null && scopes.All(granted.Contains)) covering = authorization;#            if (covering is null) covering = authorization;#OAuthConsentTests.MoreScopes_AsksAgain_NamingWhatIsAlreadyAllowed#a wider request skipped the permissions
any-users-approval#kill#ENDPOINT#            subject, applicationId, Statuses.Valid, AuthorizationTypes.Permanent, scopes: null, ct))#            null, applicationId, Statuses.Valid, AuthorizationTypes.Permanent, scopes: null, ct))#OAuthConsentTests.AnotherUsersApproval_DoesNotSkip#another user's approval skipped the permissions
revoked-approval-skips#kill#ENDPOINT#            subject, applicationId, Statuses.Valid, AuthorizationTypes.Permanent, scopes: null, ct))#            subject, applicationId, null, AuthorizationTypes.Permanent, scopes: null, ct))#OAuthConsentTests.DisconnectedApproval_DoesNotSkip#a revoked approval skipped the permissions
cancel-ignored#kill#ENDPOINT#        if (consent == "deny")#        if (consent == "never")#OAuthConsentTests.Cancel_SendsAccessDeniedToTheClient_AndRecordsNothing#cancel did not send the user back
expired-bearer-redirected#kill#ENDPOINT#                return Results.Unauthorized();#                return Results.Redirect("/connect");#OAuthConsentTests.ExpiredSession_IsUnauthorized_NotARedirect#Expected: Unauthorized
preview-leaks-request#kill#ENDPOINT#                return Results.Json(new\n                {\n                    clientName = #                return Results.Json(new\n                {\n                    clientId,\n                    clientName = #OAuthConsentTests.Preview_WithoutASession_NamesTheAppOnly#Collections differ
scoped-worker-unrestricted#kill#ENDPOINT#AssignedFlocks: flockScope.IsUnrestricted ? null#AssignedFlocks: flockScope.IsResolved ? null#OAuthConsentTests.Payload_NamesTheAssignedFlocks_OfAScopedWorker#a flock-scoped worker was told every flock
unassigned-worker-scoped#kill#ENDPOINT#AssignedFlocks: flockScope.IsUnrestricted ? null#AssignedFlocks: !flockScope.IsResolved ? null#OAuthConsentTests.Payload_SaysEveryFlock_ForAnUnassignedWorker#Expected: Null
navigation-refused#kill#ENDPOINT#            return Results.Redirect("/connect" + context.Request.QueryString);#            return Results.Unauthorized();#OAuthServerTests.AuthorizationRequest_WithoutASignedInUser_GoesToTheConsentRoute#Unauthorized
widest-default-scope#kill#ENDPOINT#asked : [OAuthScopes.ReadFarm];#asked : [.. OAuthScopes.All];#OAuthConsentTests.NoScope_AsksForReadOnly#Collections differ
redirect-not-json#kill#SERVER#if (context.RedirectUri is null || !HasBearer(request))#if (context.RedirectUri is null || HasBearer(request))#OAuthConsentTests.Approval_ReturnsTheClientRedirect_AsJson#Expected: OK
error-keeps-400#kill#SERVER#                        response.StatusCode = StatusCodes.Status200OK;\n##OAuthConsentTests.Cancel_SendsAccessDeniedToTheClient_AndRecordsNothing#Expected: OK
form-post-allowed#kill#SERVER#                    options.ResponseModes.Remove(ResponseModes.FormPost);\n##OAuthConsentTests.ResponseModeOtherThanQuery_IsRefused#Expected: BadRequest
fragment-allowed#kill#SERVER#                    options.ResponseModes.Remove(ResponseModes.Fragment);\n##OAuthConsentTests.ResponseModeOtherThanQuery_IsRefused#Expected: BadRequest
discovery-follows-host#kill#SERVER#new Uri(publicBase, context.BaseUri!.MakeRelativeUri(context.AuthorizationEndpoint!));\n                        context.TokenEndpoint = new Uri(publicBase, #new Uri(context.BaseUri!, context.BaseUri!.MakeRelativeUri(context.AuthorizationEndpoint!));\n                        context.TokenEndpoint = new Uri(context.BaseUri!, #OAuthConsentTests.Discovery_NamesEndpointsUnderTheIssuer#Strings differ
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
redirect-any-http-host#kill#REGISTRATION#uri.Host.Length != 0 || IsLoopback(uri));#uri.Host.Length != 0 || uri.Scheme == Uri.UriSchemeHttp);#OAuthClientRegistrationTests.WiderRedirectUri_IsRefused#Expected: BadRequest
redirect-fragment#hold#REGISTRATION#        && uri.Fragment.Length == 0\n##OAuthClientRegistrationTests.WiderRedirectUri_IsRefused#
localhost-refused#kill#REGISTRATION#uri.Host is "localhost" or "127.0.0.1" or "[::1]"#uri.Host is "127.0.0.1" or "[::1]"#OAuthClientRegistrationTests.AllowedRedirectUri_IsRegistered#Expected: Created
loopback-not-native#kill#REGISTRATION#        if (native)\n            descriptor.ApplicationType = ApplicationTypes.Native;\n##OAuthClientRegistrationTests.LoopbackClient_AuthorizesOnAnotherPort#Expected: OK
loopback-port-kept#kill#REGISTRATION#native ? new UriBuilder(uri) { Port = -1 }.Uri : uri#uri#OAuthClientRegistrationTests.LoopbackClient_AuthorizesOnAnotherPort#Expected: OK
loopback-query-dropped#kill#REGISTRATION#new UriBuilder(uri) { Port = -1 }#new UriBuilder(uri) { Port = -1, Query = "" }#OAuthClientRegistrationTests.LoopbackClient_AuthorizesOnAnotherPort#Expected: OK
loopback-host-merged#kill#REGISTRATION#new UriBuilder(uri) { Port = -1 }#new UriBuilder(uri) { Port = -1, Host = "localhost" }#OAuthClientRegistrationTests.LoopbackClient_OnAnotherPort_StillMatchesTheRest#Expected: BadRequest
response-normalized#kill#ENDPOINT#uri => uri.OriginalString)#uri => uri.AbsoluteUri)#OAuthClientRegistrationTests.ReturnedRedirectUri_IsUsable#Expected: OK
reserved-parameter-500#kill#ENDPOINT#catch (OpenIddictExceptions.ValidationException exception)#catch (OpenIddictExceptions.ValidationException exception) when (exception.Results.IsDefault)#OAuthClientRegistrationTests.ReservedRedirectParameter_IsARegistrationError#Expected: BadRequest
own-apps-anyones#kill#CONNECTED#db.OAuthAuthorizations.Where(authorization => authorization.Subject == subject)#db.OAuthAuthorizations.Where(authorization => authorization.Subject != null)#OAuthFailClosedTests.ConnectedApps_ListsOnlyYourOwn#Collections differ
farm-apps-not-owner-only#kill#PROGRAM#    .RequireAuthorization(AuthPolicies.OwnerOnly)\n#    .RequireAuthorization()\n#OAuthFailClosedTests.FarmConnectedApps_AreOwnerOnly#Expected: Forbidden
farm-list-every-farm#kill#CONNECTED#db.Users.Where(user => user.AccountId == accountId)#db.Users.Where(user => user.AccountId != Guid.Empty)#OAuthFailClosedTests.Owner_SeesAndDisconnectsOnlyTheirOwnFarm#Collections differ
disconnect-any-farm#kill#CONNECTED#user => user.Id == userId && user.AccountId == accountId#user => user.Id == userId#OAuthFailClosedTests.Owner_SeesAndDisconnectsOnlyTheirOwnFarm#Expected: NotFound
one-authorization-revoked#kill#CONNECTED#var revoked = await authorizations.ExecuteUpdateAsync(#var revoked = await authorizations.OrderBy(authorization => authorization.CreationDate).Take(1).ExecuteUpdateAsync(#OAuthFailClosedTests.Disconnect_RevokesEveryAuthorizationOfTheApp_AndItsTokens#authorization(s) of the app stayed valid
tokens-left-valid#kill#CONNECTED#(row.Status == Statuses.Valid || row.Status == Statuses.Inactive)#row.Status == Statuses.Rejected#OAuthFailClosedTests.Disconnect_RevokesEveryAuthorizationOfTheApp_AndItsTokens#token(s) of the app stayed valid
authorization-alone-refuses#hold#CONNECTED#(row.Status == Statuses.Valid || row.Status == Statuses.Inactive)#row.Status == Statuses.Rejected#OAuthFailClosedTests.DisconnectedApp_IsRefused_OnItsNextRequest#
disconnect-does-nothing#kill#ME#        var result = await access.DisconnectAppAsync(tenant.AccountId, currentUser.UserId, clientId, ct);#        var result = Result.Success();#OAuthFailClosedTests.DisconnectedApp_IsRefused_OnItsNextRequest#a disconnected app still worked
disconnect-unaudited#kill#CONNECTED#await audit.WriteAsync(AuditActions.UserAppDisconnected,#await audit.WriteAsync(AuditActions.UserAppConnected,#OAuthFailClosedTests.Disconnect_IsAudited_WithThePersonWhoDidIt#expected one audit row per disconnect
last-used-unstamped#kill#SERVER#                validation.AddEventHandler<OpenIddictValidationEvents.ValidateTokenContext>(handler => handler\n                    .UseScopedHandler<OAuthLastUsedStamp>()\n                    .SetOrder(ValidateAuthorizationEntry.Descriptor.Order + 2_000));\n##OAuthFailClosedTests.LastUsed_IsStampedAtMostOncePerInterval#a request did not stamp last used
last-used-every-request#kill#LAST_USED#    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);#    public static readonly TimeSpan Interval = TimeSpan.Zero;#OAuthFailClosedTests.LastUsed_IsStampedAtMostOncePerInterval#last used was stamped again within the interval
last-used-once-ever#kill#LAST_USED#\n                        || EF.Property<DateTimeOffset?>(authorization, OAuthAuthorizationConfiguration.LastUsedAtUtc)\n                            < DateTimeOffset.UtcNow - Interval))#))#OAuthFailClosedTests.LastUsed_IsStampedAtMostOncePerInterval#last used was not stamped once the interval passed
connect-unaudited#kill#ENDPOINT#            await audit.WriteAsync(reconnect ? AuditActions.UserAppReconnected : AuditActions.UserAppConnected,\n                "User", currentUser.UserId, details: new { clientId = request.ClientId, appName, scopes }, ct: token);\n##OAuthFailClosedTests.Approval_IsAudited_AsAConnection#Collections differ
widen-as-reconnect#kill#ENDPOINT#        var reconnect = covering is not null;#        var reconnect = allowed.Count > 0;#OAuthFailClosedTests.WiderApproval_IsAudited_AsAConnection#a wider approval was not recorded as a connection
reconnect-as-connect#kill#ENDPOINT#reconnect ? AuditActions.UserAppReconnected : AuditActions.UserAppConnected#AuditActions.UserAppConnected#OAuthFailClosedTests.Reconnect_IsAudited_AsAReconnection#Collections differ
grant-any#kill#REGISTRATION#                || grants.Any(grant => grant is not (GrantTypes.AuthorizationCode or GrantTypes.RefreshToken))))#))#OAuthClientRegistrationTests.WiderGrant_IsRefused#Expected: BadRequest
auth-method-any#kill#REGISTRATION#request.TokenEndpointAuthMethod is not (null or ClientAuthenticationMethods.None)#request.TokenEndpointAuthMethod is ""#OAuthClientRegistrationTests.WiderGrant_IsRefused#Expected: BadRequest
name-keeps-bidi#kill#REGISTRATION#                or UnicodeCategory.Format or UnicodeCategory.PrivateUse#                or UnicodeCategory.PrivateUse#OAuthClientRegistrationTests.ClientName_IsSanitized#Strings differ
name-uncapped#kill#REGISTRATION#elements.MoveNext() && capped.Length + elements.GetTextElement().Length <= MaxNameLength#elements.MoveNext()#OAuthClientRegistrationTests.ClientName_IsSanitized#Strings differ
discovery-silent#kill#SERVER#                        context.Metadata["registration_endpoint"] =\n                            new Uri(context.AuthorizationEndpoint, "register").AbsoluteUri;\n##OAuthClientRegistrationTests.Discovery_AdvertisesTheRegistrationEndpoint#discovery does not advertise registration_endpoint
register-unlimited#kill#ENDPOINT#            .RequireRateLimiting(RateLimitingOptions.OAuthRegisterPolicyName)\n##OAuthClientRegistrationTests.Registration_IsRateLimitedPerClientIp_OnTheSharedCounter#Collections differ
register-process-local#kill#LIMITS#            limiter.AddPolicy<string>(\n                RateLimitingOptions.OAuthRegisterPolicyName,\n                new DistributedFixedWindowPolicy(\n                    RateLimitingOptions.OAuthRegisterPolicyName,\n                    rateLimiting.OAuthRegister.PermitLimit,\n                    TimeSpan.FromSeconds(rateLimiting.OAuthRegister.WindowSeconds)));#            limiter.AddPolicy(RateLimitingOptions.OAuthRegisterPolicyName, context => RateLimitPartition.GetFixedWindowLimiter(RateLimitKey.ForClient(context.Connection.RemoteIpAddress), _ => new FixedWindowRateLimiterOptions { PermitLimit = rateLimiting.OAuthRegister.PermitLimit, Window = TimeSpan.FromSeconds(rateLimiting.OAuthRegister.WindowSeconds) }));#OAuthClientRegistrationTests.Registration_IsRateLimitedPerClientIp_OnTheSharedCounter#the registration limit did not count on the shared counter
register-reads-bearer#kill#ENDPOINT#            .WithMetadata(new IgnoresAmbientPrincipalAttribute())\n##OAuthClientRegistrationTests.Registration_IgnoresASessionBearer#Expected: Created
tokens-unpruned#kill#PURGE#tokens.PruneAsync(pruneBefore, ct)#tokens.PruneAsync(DateTimeOffset.MinValue, ct)#OAuthPurgeTests.DeadRowsPastRetention_ArePruned_AndALiveConnectionSurvives#Collections differ
authorizations-unpruned#kill#PURGE#authorizations.PruneAsync(pruneBefore, ct)#authorizations.PruneAsync(DateTimeOffset.MinValue, ct)#OAuthPurgeTests.DeadRowsPastRetention_ArePruned_AndALiveConnectionSurvives#Collection was not empty
retention-ignored#kill#PURGE#tokens.PruneAsync(pruneBefore, ct)#tokens.PruneAsync(DateTimeOffset.UtcNow, ct)#OAuthPurgeTests.DeadRowsInsideRetention_AreKept#a revoked token was pruned inside the retention
approved-app-deleted#kill#PURGE#\n                && !application.Authorizations.Any()\n                && !application.Tokens.Any())#)#OAuthPurgeTests.DeadRowsPastRetention_ArePruned_AndALiveConnectionSurvives#the purge threw
unapproved-kept#kill#PURGE#            .ExecuteDeleteAsync(ct);#            .CountAsync(ct);#OAuthPurgeTests.UnapprovedApplication_IsDeletedOnlyAfterTheWindow#an unapproved application outlived its window
window-ignored#kill#PURGE#EF.Property<DateTimeOffset>(application, OAuthApplicationConfiguration.CreatedAtUtc)\n                    < DateTimeOffset.UtcNow - unapprovedWindow\n                && ##OAuthPurgeTests.UnapprovedApplication_IsDeletedOnlyAfterTheWindow#an unapproved application was deleted inside its window
sweep-window-dropped#kill#SWEEP#PruneRetention, UnapprovedWindow, ct)#PruneRetention, TimeSpan.Zero, ct)#OAuthPurgeTests.Sweep_RunsOnlyOnTheLeader#the sweep deleted an application inside its window
follower-sweeps#kill#WORKER#            if (leadership == LeaseStatus.Follower)\n            {\n#            if (leadership == LeaseStatus.Follower)\n            {\n                if (oauthPurgeSweep is not null) await oauthPurgeSweep.RunAsync(stoppingToken);\n#OAuthPurgeTests.Sweep_RunsOnlyOnTheLeader#a follower ran the OAuth sweep
sweep-unwired#kill#WORKER#            await oauthPurgeSweep.RunAsync(ct);#            _ = oauthPurgeSweep;#OAuthPurgeTests.Sweep_RunsOnlyOnTheLeader#the leader did not run the OAuth sweep
stamp-trigger-missing#kill#STAMP_MIGRATION#                CREATE TRIGGER "TR_OpenIddictApplications_BusinessRecordTimestamps"\n                BEFORE INSERT OR UPDATE ON "OpenIddictApplications"\n                FOR EACH ROW EXECUTE FUNCTION "StampCreatedBusinessRecord"();#                SELECT 1;#OAuthApplicationCreatedAtTests.SuppliedCreatedAt_IsReplacedByTheDatabaseStamp#the database kept a supplied creation time
stamp-insert-only#kill#STAMP_MIGRATION#BEFORE INSERT OR UPDATE ON "OpenIddictApplications"#BEFORE INSERT ON "OpenIddictApplications"#OAuthApplicationCreatedAtTests.Updates_KeepCreatedAtUtc#an update changed CreatedAtUtc
stamp-sent-by-ef#kill#STAMP_CONFIG#BusinessRecordModel.ConfigureCreatedTimestamp(builder.Property<DateTimeOffset>(CreatedAtUtc));#builder.Property<DateTimeOffset>(CreatedAtUtc).ValueGeneratedOnAdd();#OAuthApplicationCreatedAtTests.EfInsert_ReadsBackTheDatabaseStamp#EF kept a creation time the database replaced
switch-new-farm-off#kill#ACCOUNT#    public bool AllowConnectedApps { get; private set; } = true;#    public bool AllowConnectedApps { get; private set; }#OAuthFailClosedTests.ConnectedApps_AreOnByDefault_ForNewAndExistingFarms#a new farm starts with connected apps off
switch-existing-farms-off#kill#SWITCH_MIGRATION#                defaultValue: true);#                defaultValue: false);#OAuthFailClosedTests.ConnectedApps_AreOnByDefault_ForNewAndExistingFarms#the migration turned connected apps off
switch-not-owner-only#kill#ACCOUNT_ENDPOINTS#        group.MapPut("/connected-apps", SetConnectedApps)\n            .RequireAuthorization(AuthPolicies.OwnerOnly)#        group.MapPut("/connected-apps", SetConnectedApps)\n            .RequireAuthorization()#OAuthFailClosedTests.ConnectedAppsSwitch_IsOwnerOnly#Expected: Forbidden
switch-token-unchecked#kill#VERIFIER#        if (connectedApp && credentialState.AccountAllowsConnectedApps != true)\n            return CredentialVerdict.ConnectedAppsOff;\n##OAuthFailClosedTests.ConnectedAppsOff_RefusesAnExistingToken_OnTheNextRequest#Expected: Unauthorized
switch-token-unrecognised#kill#EPOCH#                    context.User.HasClaim(claim => claim.Type == OpenIddictConstants.Claims.ClientId),#                    false,#OAuthFailClosedTests.ConnectedAppsOff_RefusesAnExistingToken_OnTheNextRequest#Expected: Unauthorized
switch-refuses-sessions#kill#EPOCH#                    context.User.HasClaim(claim => claim.Type == OpenIddictConstants.Claims.ClientId),#                    true,#OAuthFailClosedTests.ConnectedAppsOff_LeavesSessionsAlone#Expected: OK
switch-off-sticks#kill#ACCOUNT#        AllowConnectedApps = allow;#        AllowConnectedApps = AllowConnectedApps && allow;#OAuthFailClosedTests.ConnectedAppsBackOn_RestoresTheConnection#Expected: OK
switch-consent-unchecked#kill#ENDPOINT#        if ((await farm.GetSettingsAsync(ct))?.AllowConnectedApps != true)\n            return Results.Problem("This farm doesn't allow connected apps. Ask an Owner.",\n                statusCode: StatusCodes.Status403Forbidden, title: ConnectedAppsOff);\n##OAuthFailClosedTests.ConnectedAppsOff_RefusesConsent_BeforeThePassword_AndIssuesNoCode#Expected: Forbidden
switch-registration-checked#kill#ENDPOINT#        ClientRegistrationRequest request, IOpenIddictApplicationManager applications, CancellationToken ct)\n    {\n#        ClientRegistrationRequest request, IOpenIddictApplicationManager applications, IFarmModule farm, CancellationToken ct)\n    {\n        if ((await farm.GetSettingsAsync(ct))?.AllowConnectedApps != true) return RegistrationError("access_denied", "off");\n#OAuthFailClosedTests.ConnectedAppsOff_LeavesRegistrationAlone#Expected: Created
switch-unaudited#kill#SWITCH_HANDLER#                before = new { AllowConnectedApps = before },#                before = new { },#OAuthFailClosedTests.ConnectedAppsSwitch_IsAudited_WithTheOwnerAsActor#Expected: "true"
switch-race-unversioned#kill#ACCOUNT#        AllowConnectedApps = allow;\n        Version++;#        AllowConnectedApps = allow;#OAuthFailClosedTests.ConnectedAppsSwitch_TwoOwnersAtOneVersion_ExactlyOneWins#Expected: 1
cimd-mixed-answer-admitted#kill#FETCHER#!addresses.All(address => IsPublic(address)#!addresses.Any(address => IsPublic(address)#ClientMetadataFetcherTests.MixedAnswer_IsRefused_WhicheverAddressComesFirst#Strings differ
cimd-mapped-admitted#kill#FETCHER#!address.IsIPv4MappedToIPv6 && GlobalUnicastV6.Contains(address)#GlobalUnicastV6.Contains(address)#ClientMetadataFetcherTests.SpecialUseAddress_IsRefused_BeforeAnyConnection#Strings differ
cimd-v6-not-allow-list#kill#FETCHER#!address.IsIPv4MappedToIPv6 && GlobalUnicastV6.Contains(address)#!address.IsIPv4MappedToIPv6#ClientMetadataFetcherTests.SpecialUseAddress_IsRefused_BeforeAnyConnection#Strings differ
cimd-private-v4-admitted#kill#FETCHER#"0.0.0.0/8", "10.0.0.0/8", #"0.0.0.0/8", #ClientMetadataFetcherTests.SpecialUseAddress_IsRefused_BeforeAnyConnection#Strings differ
cimd-link-local-admitted#kill#FETCHER#"127.0.0.0/8", "169.254.0.0/16", #"127.0.0.0/8", #ClientMetadataFetcherTests.SpecialUseAddress_IsRefused_BeforeAnyConnection#Strings differ
cimd-cgnat-admitted#kill#FETCHER#"10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8"#"10.0.0.0/8", "127.0.0.0/8"#ClientMetadataFetcherTests.SpecialUseAddress_IsRefused_BeforeAnyConnection#Strings differ
cimd-rebinding#kill#FETCHER#return await transport.Dial(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), ct);#return await transport.Dial(new IPEndPoint((await transport.Resolve(host, ct))[0], context.DnsEndPoint.Port), ct);#ClientMetadataFetcherTests.Connection_IsPinnedToTheVettedAddress#metadata.unreachable
cimd-private-hosts-any-name#kill#FETCHER#|| privateHosts.Contains(host) && #|| privateHosts.Count > 0 && #ClientMetadataFetcherTests.PrivateHosts_AdmitsOnlyTheNamedHost_OnAPrivateNetwork#Values differ
cimd-private-hosts-loopback#kill#FETCHER#{ "10.0.0.0/8", "100.64.0.0/10", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7" }#{ "0.0.0.0/0", "fc00::/7" }#ClientMetadataFetcherTests.PrivateHosts_AdmitsOnlyTheNamedHost_OnAPrivateNetwork#Values differ
cimd-redirect-followed#kill#FETCHER#AllowAutoRedirect = false,#AllowAutoRedirect = true,#ClientMetadataFetcherTests.Redirect_IsNotFollowed#Strings differ
cimd-any-2xx#kill#FETCHER#if (response.StatusCode != HttpStatusCode.OK)#if (!response.IsSuccessStatusCode)#ClientMetadataFetcherTests.OnlyOk_IsAccepted#Strings differ
cimd-content-type-unchecked#kill#FETCHER#if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))#if (response.Content.Headers.ContentType is null)#ClientMetadataFetcherTests.OtherContentType_IsRefused#Strings differ
cimd-stream-uncapped#kill#FETCHER#            if (length > MaxBodyBytes)\n#            if (length > MaxBodyBytes + 1)\n#ClientMetadataFetcherTests.StreamedBody_IsCappedWhileReading#Values differ
cimd-declared-length-unchecked#kill#FETCHER#if (response.Content.Headers.ContentLength > MaxBodyBytes)#if (response.Content.Headers.ContentLength > MaxBodyBytes * 10_000)#ClientMetadataFetcherTests.DeclaredLength_OverTheCap_IsRefusedBeforeReading#Strings differ
cimd-body-no-deadline#kill#FETCHER#body.ReadAsync(buffer.AsMemory(length), timeout.Token)#body.ReadAsync(buffer.AsMemory(length), ct)#ClientMetadataFetcherTests.SlowBody_StopsAtTheDeadline#Strings differ
cimd-no-deadline#kill#FETCHER#        timeout.CancelAfter(_deadline);\n##ClientMetadataFetcherTests.SlowConnect_StopsAtTheDeadline#the connect held the fetch
cimd-cookies-kept#kill#FETCHER#UseCookies = false,#UseCookies = true,#ClientMetadataFetcherTests.Request_CarriesNoCookie_AndAnHonestUserAgent#Collections differ
cimd-proxy-used#kill#FETCHER#UseProxy = false,#UseProxy = true,#ClientMetadataFetcherTests.Handler_FollowsNoRedirect_UsesNoProxy_KeepsNoCookies#Assert.False() Failure
cimd-lifetime-unclamped#kill#FETCHER#Math.Clamp((cacheControl?.MaxAge ?? Default).Ticks, Floor.Ticks, Ceiling.Ticks)#(cacheControl?.MaxAge ?? Default).Ticks#ClientMetadataFetcherTests.Lifetime_FollowsCacheControl_WithinTheBounds#Values differ
cimd-no-cache-kept-long#kill#FETCHER#cacheControl is { NoStore: true } or { NoCache: true }#cacheControl is { NoStore: true }#ClientMetadataFetcherTests.Lifetime_FollowsCacheControl_WithinTheBounds#Values differ
cimd-client-id-case#kill#REGISTRATION#|| clientId.GetString() != documentUrl)#|| !string.Equals(clientId.GetString(), documentUrl, StringComparison.OrdinalIgnoreCase))#ClientMetadataTests.Document_NamingAnotherClientId_IsRefused#Strings differ
cimd-secret-allowed#kill#REGISTRATION#if (root.TryGetProperty("client_secret", out _) || root.TryGetProperty("client_secret_expires_at", out _))#if (root.TryGetProperty("client_secret_x", out _))#ClientMetadataTests.UntrustedDocument_IsRefused#Strings differ
cimd-duplicate-keys#kill#REGISTRATION#AllowDuplicateProperties = false#AllowDuplicateProperties = true#ClientMetadataTests.UntrustedDocument_IsRefused#Strings differ
cimd-url-noncanonical#kill#REGISTRATION#|| url.AbsolutePath == "/" || url.AbsoluteUri != clientId)#|| url.AbsolutePath == "/")#ClientMetadataTests.OtherForms_AreRefused#Strings differ
cimd-url-query#kill#REGISTRATION#url.Query.Length != 0 || ##ClientMetadataTests.OtherForms_AreRefused#Strings differ
cimd-url-ip-literal#kill#REGISTRATION# || url.HostNameType != UriHostNameType.Dns##ClientMetadataTests.OtherForms_AreRefused#Strings differ
cimd-url-length#kill#REGISTRATION#if (clientId.Length > MaxDocumentUrlLength)#if (clientId.Length > 2 * MaxDocumentUrlLength)#ClientMetadataTests.Url_LongerThanTheColumn_IsRefused#Strings differ
cimd-cache-ignored#kill#DOCUMENTS#ExpiresAt(await applications.GetPropertiesAsync(row, ct)) > clock.GetUtcNow())#ExpiresAt(await applications.GetPropertiesAsync(row, ct)) > DateTimeOffset.MaxValue)#OAuthFailClosedTests.StoredCopy_IsReused_UntilItExpires#Assert.Single() Failure
cimd-never-refetched#kill#DOCUMENTS#ExpiresAt(await applications.GetPropertiesAsync(row, ct)) > clock.GetUtcNow())#ExpiresAt(await applications.GetPropertiesAsync(row, ct)) > DateTimeOffset.MinValue)#OAuthFailClosedTests.Refetch_AppliesTheNewDocument#Expected: BadRequest
cimd-update-skipped#kill#DOCUMENTS#                await applications.UpdateAsync(row, descriptor.Value, ct);#                await Task.CompletedTask;#OAuthFailClosedTests.Refetch_AppliesTheNewDocument#Expected: BadRequest
cimd-per-url-budget#kill#DOCUMENTS#.Count <= PerUrlBudget.Limit\n#.Count <= PerUrlBudget.Limit * 10\n#OAuthFailClosedTests.FailingUrl_IsFetchedOnlyWithinItsBudget#Expected: 10
cimd-global-budget#kill#DOCUMENTS#.Count <= GlobalBudget.Limit;#.Count <= GlobalBudget.Limit * 10;#OAuthFailClosedTests.ManyUrls_ShareOneGlobalBudget#Expected: 60
cimd-race-unhandled#kill#DOCUMENTS#catch (Exception exception) when (exception is OpenIddictExceptions.ValidationException\n            or OpenIddictExceptions.ConcurrencyException\n            || exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } })#catch (Exception exception) when (exception is OpenIddictExceptions.ConcurrencyException)#OAuthFailClosedTests.ConcurrentFirstRequests_StoreOneRow#Assert.All() Failure
cimd-issuer-in-redirect-stored#kill#DOCUMENTS#                return Refuse(Errors.InvalidRequest, string.Join(" ", validation.Results.Select(result => result.ErrorMessage)));#                return Result.Success();#OAuthFailClosedTests.DocumentWithAnIssuerInItsRedirect_IsRefused_AndNotStored#Assert.Contains() Failure
cimd-disabled-still-fetches#kill#DOCUMENTS#        if (!options.Enabled)#        if (options.Enabled && !options.Enabled)#OAuthFailClosedTests.TurnedOff_NeitherAdvertisesNorFetches#Expected: BadRequest
cimd-not-advertised#kill#SERVER#context.Metadata["client_id_metadata_document_supported"] = true;#context.Metadata["client_id_metadata_document_supported"] = false;#OAuthFailClosedTests.Discovery_AdvertisesMetadataDocuments#discovery does not advertise
cimd-handler-unregistered#kill#SERVER#                server.AddEventHandler(ClientMetadataDocuments.Descriptor);\n##OAuthFailClosedTests.MetadataClient_ConnectsListsAuditsAndDisconnects_LikeAnyOther#Expected: OK
cimd-consent-domain-hidden#kill#ENDPOINT#                ClientMetadata.VerifiedDomain(request.ClientId),#                null,#OAuthFailClosedTests.Consent_AndPreview_NameTheVerifiedDomain#Strings differ
cimd-preview-domain-hidden#kill#ENDPOINT#                    verifiedDomain = ClientMetadata.VerifiedDomain(clientId),#                    verifiedDomain = (string?)null,#OAuthFailClosedTests.Consent_AndPreview_NameTheVerifiedDomain#Strings differ
cimd-disconnect-by-path#kill#ME#group.MapDelete("/connected-apps", DisconnectApp)#group.MapDelete("/connected-apps/{clientId}", DisconnectApp)#OAuthFailClosedTests.MetadataClient_ConnectsListsAuditsAndDisconnects_LikeAnyOther#Expected: NoContent
cimd-interrupted-body-escapes#kill#FETCHER#catch (Exception exception) when (exception is HttpRequestException or IOException)#catch (HttpRequestException)#ClientMetadataFetcherTests.InterruptedBody_IsUnreachable#the fetch threw
cimd-as112-v6-admitted#kill#FETCHER#"2002::/16", "2620:4f:8000::/48", #"2002::/16", #ClientMetadataFetcherTests.SpecialUseAddress_IsRefused_BeforeAnyConnection#Strings differ
cimd-lone-surrogate-escapes#kill#REGISTRATION#catch (Exception exception) when (exception is JsonException or InvalidOperationException)#catch (JsonException)#ClientMetadataTests.LoneSurrogate_InAnyStringField_IsInvalidMetadata#the document threw
cimd-client-id-unchecked#kill#REGISTRATION#\n                || clientId.GetString() != documentUrl)#)#ClientMetadataTests.Document_NamingAnotherClientId_IsRefused#Strings differ
cimd-duplicate-client-refused#kill#DOCUMENTS#                && !(row is null && IsOnlyDuplicateClientId(validation) && await StoredMeanwhileAsync(clientId, ct)))#)#OAuthFailClosedTests.SecondRequest_MeetingTheFirstsCopy_UsesIt#Expected: OK
cimd-stale-fallback-restored#kill#DOCUMENTS#            return Refuse(Errors.TemporarilyUnavailable, "Too many metadata documents were fetched recently. Try again later.");#            return row is not null ? Result.Success() : Refuse(Errors.TemporarilyUnavailable, "Too many metadata documents were fetched recently. Try again later.");#OAuthFailClosedTests.RemovedDocument_IsNotRevived_ByDrainingTheBudget#Expected: BadRequest
cimd-mixed-validation-recovered#kill#DOCUMENTS#row is null && IsOnlyDuplicateClientId(validation) && #row is null && #OAuthFailClosedTests.SecondRequest_WithAnInvalidDocument_IsRefused_DespiteTheFirstsCopy#Expected: BadRequest
cimd-idempotency-query-ignored#kill#IDEMPOTENCY#        if (request.QueryString.HasValue)#        if (request.ContentLength < 0)#OAuthFailClosedTests.Disconnect_ReusingAKeyForAnotherApp_IsAConflict_NotAReplay#Expected: Conflict
cimd-response-type-fetched#kill#DOCUMENTS#if (context.Request.ResponseType != ResponseTypes.Code)#if (context.Request.ResponseType == "unchecked")#OAuthFailClosedTests.MalformedRequest_IsRefused_BeforeAnyFetch#Assert.Empty() Failure
cimd-pkce-fetched#kill#DOCUMENTS#if (string.IsNullOrEmpty(context.Request.CodeChallenge) || context.Request.CodeChallengeMethod != CodeChallengeMethods.Sha256)#if (context.Request.CodeChallengeMethod == "unchecked")#OAuthFailClosedTests.MalformedRequest_IsRefused_BeforeAnyFetch#Assert.Empty() Failure
cimd-approved-shares-strangers-budget#kill#DOCUMENTS#approved: await IsApprovedAsync(scope, applications, row, ct)#approved: false#OAuthFailClosedTests.ApprovedApp_IsRefreshed_PastADrainedGlobalBudget#an approved app was refused
cimd-everyone-gets-approved-budget#kill#DOCUMENTS#approved: await IsApprovedAsync(scope, applications, row, ct)#approved: true#OAuthFailClosedTests.ApprovedApp_IsRefreshed_PastADrainedGlobalBudget#Strings differ
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

FILES=("$IDENTITY" "$TELEMETRY" "$SERVER" "$ENDPOINT" "$REGISTRATION" "$FETCHER" "$DOCUMENTS" "$IDEMPOTENCY" "$LIMITS" "$PURGE" "$SWEEP" "$WORKER" "$STAMP_MIGRATION" "$STAMP_CONFIG" "$VERIFIER" "$EPOCH" "$MUST_CHANGE" "$CONNECTED" "$LAST_USED" "$ME" "$PROGRAM" "$ACCOUNT" "$ACCOUNT_ENDPOINTS" "$SWITCH_HANDLER" "$SWITCH_MIGRATION")
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
  # MUTANT_FILTER (a bash regex over mutant names) runs a subset; baseline and restore still run.
  [[ -n "${MUTANT_FILTER:-}" && ! "$name" =~ $MUTANT_FILTER ]] && continue
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
