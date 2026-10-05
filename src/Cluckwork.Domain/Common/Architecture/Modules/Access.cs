namespace Cluckwork.Domain.Common.Architecture;

[ModuleOwner("Access", "module",
    Namespaces = [
        "Cluckwork.Application.Features.Users",
        "Cluckwork.Infrastructure.Identity",
    ],
    Implementations = [
        "Cluckwork.Infrastructure.Repositories.UserRoleAssignmentRepository",
    ],
    Types = [
        "Cluckwork.Application.Common.IIdentityProvider",
        "Cluckwork.Application.Common.IStepUpGrantService",
    ])]
[ModuleEdge(
    "Access", "Commerce", "W",
    "W because AccountProvisioner inserts EggUnitConversion.Defaults for a new farm inside its provisioning transaction; the rest of the cell reads. Identity carries the per-user stepper-unit preference: ApplicationUser stores Domain.Catalog.EggUnit, IIdentityProvider, the Identity port Access claims, takes it in SetStepperUnitAsync, IdentityProvider reads and writes it, SetStepperUnitHandler resolves it through Commerce's IEggUnitConversionLookup port (#854) and rejects inactive conversions, and SetStepperUnitValidator validates only the enum name. Nothing prevents a stored preference from pointing at a conversion later deactivated through UpdateEggUnitConversionHandler. Design 3.4 shows Access -> Commerce as none; this is live coupling the target design has still to remove.",
    "Cluckwork.Application.Common.IIdentityProvider",
    "Cluckwork.Application.Features.Users.SetStepperUnit.SetStepperUnitHandler",
    "Cluckwork.Application.Features.Users.SetStepperUnit.SetStepperUnitValidator",
    "Cluckwork.Infrastructure.Identity.AccountProvisioner",
    "Cluckwork.Infrastructure.Identity.ApplicationUser",
    "Cluckwork.Infrastructure.Identity.IdentityProvider")]
[ModuleEdge(
    "Access", "EggOperations", "W",
    "W because AccountProvisioner inserts a new farm's default grades, Domain.Eggs.EggGrade.Defaults(accountId, SeedDefaults.FarmId), inside its provisioning transaction, so farm provisioning writes Egg Operations' table and depends on its grade shape. Design 3.4 shows Access -> Egg Ops as none; this is live coupling the target design has still to remove.",
    "Cluckwork.Infrastructure.Identity.AccountProvisioner")]
[ModuleEdge(
    "Access", "Farm", "W",
    "W because AccountProvisioner inserts the new Account inside its provisioning transaction and AccountRenameService calls Account.Rename and saves; the rest of the cell reads. Identity is where a farm is created, renamed, suspended and recovered. AccountProvisioner, AccountRenameService, AccountSuspensionService, AdminRecoveryService, FirstRunAdminService and FirstRunStatusService load Domain.Accounts.Account or inject Farm's IAccountRepository, and IdentityProvider takes that repository by fully qualified name for the account-scoped login (#532). CredentialEpochVerifier reads Account.IsActive in the same fresh per-request query as the user's credential epoch, so a suspended farm's credentials stop working at once (#364, #579, #857). The user validators read Domain.Accounts.Roles for the assignable role set, and IUserRoleAssignmentRepository returns Domain.Accounts.UserRoleAssignment rows, which AccessModule reads to list a user's flock assignments. IAccessLookup returns Domain.Accounts.EffectiveAccountRole, and AccessLookup resolves it with Roles.ResolveEffective in its private effective-role routine (#612, #857). Design 3.4 row Access -> Farm = R. AccessFixture (#858) creates the simulation fixture's UserRoleAssignment through the scoped context, and AccessSeedLookup checks the fixture's Owner role by Roles.Owner.",
    "Cluckwork.Application.Features.Users.AssignFlock.AssignFlockHandler",
    "Cluckwork.Application.Features.Users.AssignFlock.UnassignFlockHandler",
    "Cluckwork.Application.Features.Users.ChangeUserRole.ChangeUserRoleValidator",
    "Cluckwork.Application.Features.Users.CreateUser.CreateUserValidator",
    "Cluckwork.Application.Features.Users.IAccessLookup",
    "Cluckwork.Application.Features.Users.IUserRoleAssignmentRepository",
    "Cluckwork.Infrastructure.Identity.AccessFixture",
    "Cluckwork.Infrastructure.Identity.AccessLookup",
    "Cluckwork.Infrastructure.Identity.AccessModule",
    "Cluckwork.Infrastructure.Identity.AccessSeedLookup",
    "Cluckwork.Infrastructure.Identity.AccountProvisioner",
    "Cluckwork.Infrastructure.Identity.AccountRenameService",
    "Cluckwork.Infrastructure.Identity.AccountSuspensionService",
    "Cluckwork.Infrastructure.Identity.AdminRecoveryService",
    "Cluckwork.Infrastructure.Identity.CredentialEpochVerifier",
    "Cluckwork.Infrastructure.Identity.FirstRunAdminService",
    "Cluckwork.Infrastructure.Identity.FirstRunStatusService",
    "Cluckwork.Infrastructure.Identity.IdentityProvider")]
[ModuleEdge(
    "Access", "FlockManagement", "R",
    "AssignFlockHandler injects Flock Management's IFlockLookup port to prove the flock exists before narrowing a worker to it and to name it in the audit row (#103, spec 5.2/5.3). Design 3.4 row Access -> Flock = R. AccessFixture (#858) reads the same port for the simulation fixture's assignment and audit row. AccessModule names a user's flock assignments through IFlockLookup.GetDisplayNamesAsync, one bounded read whose filters decide which names the caller sees (#613, #859).",
    "Cluckwork.Application.Features.Users.AssignFlock.AssignFlockHandler",
    "Cluckwork.Infrastructure.Identity.AccessFixture",
    "Cluckwork.Infrastructure.Identity.AccessModule")]
internal static class AccessModuleRules { }
