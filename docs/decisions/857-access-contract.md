# #857 — Access contract

**Status:** accepted
**Date:** 2026-10-04

No incident. Epic #514, Track C gives Access an aggregate-free contract and
converts its adapters and peer authorization reads without changing token
minting, middleware order, transaction boundaries or fixture attribution.

## Boundary

The Access ledger declares 28 top-level types under
`Cluckwork.Application.Features.Users`:

| Purpose | Contract entries |
|---|---|
| Ports (8) | `IAccessModule`, `IAccessLookup`, `ICredentialEpochVerifier`, `IRefreshTokenPurge`, `IAccessOperations`, `IAccessAccountLifecycle`, `IAccessSeedLookup`, `IAccessFixture` (#858 P5) |
| Values (9) | `CredentialVerdict`, `UserFlockAssignment`, `AccessActor`, `AccessUserSummary`, `FirstRunAdminOutcome`, `AdminRecoveryResult`, `AccountProvisionOutcome`, `AccountLifecycleOutcome`, `AccountRenameOutcome` |
| Commands (12) | `CreateUser.CreateUserCommand`, `UpdateUser.UpdateUserCommand`, `SetUserPassword.SetUserPasswordCommand`, `ChangeUserRole.ChangeUserRoleCommand`, `ChangeUserEmail.ChangeUserEmailCommand`, `DisableUser.DisableUserCommand`, `EnableUser.EnableUserCommand`, `AssignFlock.AssignFlockCommand`, `AssignFlock.UnassignFlockCommand`, `SetLanguage.SetLanguageCommand`, `SetStepperUnit.SetStepperUnitCommand`, `ChangeOwnPassword.ChangeOwnPasswordCommand` |

`IIdentityProvider` and `IStepUpGrantService` remain in Application.Common but
are claimed by Access through the ledger's `types`. They are implementation
ports, outside the contract. Removing either claim reopens the peer and adapter
bypass described in [#1023](1023-peer-contract-guard.md). `ICurrentUser`,
`IFlockScopeGuard` and `SystemActors` stay Platform-owned.

`UserRoleAssignmentRepository` is a listed Access implementation.
`IUserRoleAssignmentRepository` stays outside the contract: its aggregate
surface must never become an adapter allowance. The recursive contract guard,
peer guard, adapter guard and compatibility guard check different boundaries;
none replaces the others.

## Authorization and construction

`IAccessLookup.GetAssignedFlocksAsync` returns null for zero rows or any
null-flock row, and the complete set otherwise. Null means unrestricted. Read
failures must propagate. The tenant refusal precedes the untracked query. The
port keeps the same scoped context as its callers and reads fresh committed
assignments. Its caller supplies the resolved actor or a verified tenant member;
an unknown or foreign user id has zero visible assignments too. Confirm's order
remains account lock, order lock, effective role,
assignments, FIFO stock lock. Assignment admission validates step-up before
target or effective-role reads and keeps the identity target-list read.

`IAccessAccountLifecycle` contains only suspend, reactivate and rename. Its
constructor graph must not build UserManager, IdentityProvider or Redis,
including when a real lifecycle CLI verb is resolved. Bootstrap, recovery and
provisioning stay on `IAccessOperations`; fixture creation forwards once with
`mustChangePassword:false`. The provisioning outcome moves without a copy or
field conversion.

`IAccessOperations` is for operator CLI verbs and simulation creation. It is
registered in Production and its creation operation bypasses interactive
step-up. The contract guard permits other adapters and peers to take this port;
only review enforces that caller restriction. Interactive creation uses
`IAccessModule` and retains its handler's proof check.

`IAccessModule` still delegates session and profile operations. A deeper session
reshape suggested during #1041 review is deferred: splitting the sign-in
sequence or token result requires its own design and authentication trace.

## Seed composition

Both seeders read actors through `IAccessSeedLookup`, registered only outside
Production beside the seeders. The port uses the account directory, UserManager
and scoped context. Demo's reads must not construct the operations write graph,
IdentityProvider or Redis. Simulation alone uses `IAccessOperations.CreateUserAsync`.

The fixture read port accepts an explicit account id for cross-farm composition.
Its registration is absent in Production, but present in Development, Testing
and Staging. Only review limits its callers to the two seeders; the contract
guard permits an adapter or peer to take it in those environments.

Email lookup keeps Identity normalization and an explicit account predicate.
Role members include disabled users and sort by Id. Each seeder selects the
lowest-Id active Owner, counts disabled Owners separately, then rereads that
Owner's complete actual roles at the original post-preflight position. This
adds one account-scoped Users statement per selected Owner. A missing reread
fails; it never creates a fallback or system actor.

`CurrentUserContext` and `SimulationOptions` move unchanged from Identity to
Persistence. The scoped alias must resolve `ICurrentUser` and
`CurrentUserContext` to the same instance, with independent instances in other
scopes. Its roles remain authorization input, not merely audit labels.

The assignment write stays in simulation composition until #858 P5: the same
untracked, tenant-filtered, UserId-filtered, Id-ordered read, entity factory,
Owner actor and flock-name audit, followed by one save. No early save or new
transaction is permitted. Five Access compatibility rows expire at #858:
three AspNetRoles prerequisites, one AspNetUsers total count, and that
UserRoleAssignments write. The assignment repository's single filtered LEFT
JOIN into Flocks instead expires at #859; a second lookup would change its
query shape.

**Amended by #858 P5.** The write moved to `IAccessFixture`, registered only
outside Production. It keeps the read, factory, Owner actor, flock-name audit
and single save above, and adds one account-scoped check that the user belongs
to the named farm, because no foreign key ties an assignment's user to its
farm. A flock outside the resolved tenant reads as missing, and the tenant stamp
refuses a farm other than the resolved one. `IAccessSeedLookup` gained the
Owner-role existence check and the farm's user count. Four of the five Access
rows are gone; demo's Owner-role prerequisite remains for #858 P6.

The #280/#500 parity proof includes demo's exact 977 action/actor rows and
simulation's exact cast, six products, all count and lifecycle fields, complete
action-to-actor attribution and worker rotation. Both baseline and new code
run every sibling class in DemoSeedActorTests and SimulationCrossDayRerunTests,
plus SimulationSeederTests. Durable anchor, completion ordering, clean rerun
fingerprint, partial rerun and changed-definition outcomes remain required.

## Limits

The ownership guard reads parameter types and service resolutions, including
generic arguments. Method-body type uses and Platform helper indirection retain
#1023's documented limits. The compatibility rows acknowledge remaining
composition reads; they do not authorize a broader security bypass. #858 owns
fixture composition and #859 owns the remaining cross-module join.
