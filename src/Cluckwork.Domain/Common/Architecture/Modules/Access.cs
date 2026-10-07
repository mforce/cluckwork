namespace Cluckwork.Domain.Common.Architecture;

[ModuleOwner("Access", "module",
    Namespaces = [
        "Cluckwork.Application.Modules.Access",
        "Cluckwork.Infrastructure.Modules.Access",
    ])]
[ModuleEdge(
    "Access", "Commerce", "W",
    "W because AccountProvisioner stages a new farm's default unit conversions through Commerce's IEggUnitConversionProvisioning port inside its provisioning transaction (#1116); the rest of the cell reads. Identity carries the per-user stepper-unit preference: ApplicationUser stores Commerce's EggUnit, IIdentityProvider, the Identity port Access claims, takes it in SetStepperUnitAsync, IdentityProvider reads and writes it, SetStepperUnitHandler resolves it through Commerce's IEggUnitConversionLookup port (#854) and rejects inactive conversions, and SetStepperUnitValidator validates only the enum name. Nothing prevents a stored preference from pointing at a conversion later deactivated through UpdateEggUnitConversionHandler. Design 3.4 shows Access -> Commerce as none; this is live coupling the target design has still to remove. ApplicationUserConfiguration maps the stepper-unit preference column as Commerce's EggUnit enum name (#1097). The Access contract's UserProfile returns that preference to the /me adapter typed as Commerce's published Cluckwork.Domain.Modules.Commerce.Contracts.EggUnit, as Farm's FarmSettingsDetails returns the farm default (#1103).",
    "Cluckwork.Application.Modules.Access.Contracts.UserProfile",
    "Cluckwork.Application.Modules.Access.Users.IIdentityProvider",
    "Cluckwork.Application.Modules.Access.Users.SetStepperUnit.SetStepperUnitHandler",
    "Cluckwork.Application.Modules.Access.Users.SetStepperUnit.SetStepperUnitValidator",
    "Cluckwork.Infrastructure.Modules.Access.Identity.AccountProvisioner",
    "Cluckwork.Infrastructure.Modules.Access.Identity.ApplicationUser",
    "Cluckwork.Infrastructure.Modules.Access.Identity.IdentityProvider",
    "Cluckwork.Infrastructure.Modules.Access.Configurations.ApplicationUserConfiguration")]
[ModuleEdge(
    "Access", "EggOperations", "W",
    "W because AccountProvisioner stages a new farm's default grades through Egg Operations' IEggGradeProvisioning port inside its provisioning transaction (#1116), so farm provisioning writes Egg Operations' table while Egg Operations builds the rows and owns their shape. Design 3.4 shows Access -> Egg Ops as none; this is live coupling the target design has still to remove.",
    "Cluckwork.Infrastructure.Modules.Access.Identity.AccountProvisioner")]
[ModuleEdge(
    "Access", "Farm", "W",
    "W because AccountProvisioner inserts the new Account inside its provisioning transaction and AccountRenameService calls Account.Rename and saves; the rest of the cell reads. Identity is where a farm is created, renamed, suspended and recovered. AccountProvisioner, AccountRenameService, AccountSuspensionService, AdminRecoveryService, FirstRunAdminService and FirstRunStatusService load Farm's Account or inject Farm's IAccountRepository, and IdentityProvider takes that repository by fully qualified name for the account-scoped login (#532). CredentialEpochVerifier reads Account.IsActive in the same fresh per-request query as the user's credential epoch, so a suspended farm's credentials stop working at once (#364, #579, #857). The user validators read Farm's Roles for the assignable role set, and IUserRoleAssignmentRepository returns Farm's UserRoleAssignment rows, which AccessModule reads to list a user's flock assignments. IAccessLookup returns Farm's EffectiveAccountRole, and AccessLookup resolves it with Roles.ResolveEffective in its private effective-role routine (#612, #857). Design 3.4 row Access -> Farm = R. AccessFixture (#858) creates the simulation fixture's UserRoleAssignment through the scoped context, and AccessSeedLookup checks the fixture's Owner role by Roles.Owner. Access owns the UserRoleAssignments table (TableOwnerOverrides), so its mapping and store read and write Farm's UserRoleAssignment entity: UserRoleAssignmentConfiguration configures it and UserRoleAssignmentRepository lists, adds and removes it. FlockScopeGuard, declared beside the repository, decides whether a Worker is narrowed to assigned flocks through Roles.ResolveEffective and EffectiveAccountRole (#1097).",
    "Cluckwork.Application.Modules.Access.Users.AssignFlock.AssignFlockHandler",
    "Cluckwork.Application.Modules.Access.Users.AssignFlock.UnassignFlockHandler",
    "Cluckwork.Application.Modules.Access.Users.ChangeUserRole.ChangeUserRoleValidator",
    "Cluckwork.Application.Modules.Access.Users.CreateUser.CreateUserValidator",
    "Cluckwork.Application.Modules.Access.Contracts.IAccessLookup",
    "Cluckwork.Application.Modules.Access.Users.IUserRoleAssignmentRepository",
    "Cluckwork.Infrastructure.Modules.Access.Identity.AccessFixture",
    "Cluckwork.Infrastructure.Modules.Access.Identity.AccessLookup",
    "Cluckwork.Infrastructure.Modules.Access.Identity.AccessModule",
    "Cluckwork.Infrastructure.Modules.Access.Identity.AccessSeedLookup",
    "Cluckwork.Infrastructure.Modules.Access.Identity.AccountProvisioner",
    "Cluckwork.Infrastructure.Modules.Access.Identity.AccountRenameService",
    "Cluckwork.Infrastructure.Modules.Access.Identity.AccountSuspensionService",
    "Cluckwork.Infrastructure.Modules.Access.Identity.AdminRecoveryService",
    "Cluckwork.Infrastructure.Modules.Access.Identity.CredentialEpochVerifier",
    "Cluckwork.Infrastructure.Modules.Access.Identity.FirstRunAdminService",
    "Cluckwork.Infrastructure.Modules.Access.Identity.FirstRunStatusService",
    "Cluckwork.Infrastructure.Modules.Access.Identity.IdentityProvider",
    "Cluckwork.Infrastructure.Modules.Access.Configurations.UserRoleAssignmentConfiguration",
    "Cluckwork.Infrastructure.Modules.Access.Repositories.FlockScopeGuard",
    "Cluckwork.Infrastructure.Modules.Access.Repositories.UserRoleAssignmentRepository")]
[ModuleEdge(
    "Access", "FlockManagement", "R",
    "AssignFlockHandler injects Flock Management's IFlockLookup port to prove the flock exists before narrowing a worker to it and to name it in the audit row (#103, spec 5.2/5.3). Design 3.4 row Access -> Flock = R. AccessFixture (#858) reads the same port for the simulation fixture's assignment and audit row. AccessModule names a user's flock assignments through IFlockLookup.GetDisplayNamesAsync, one bounded read whose filters decide which names the caller sees (#613, #859).",
    "Cluckwork.Application.Modules.Access.Users.AssignFlock.AssignFlockHandler",
    "Cluckwork.Infrastructure.Modules.Access.Identity.AccessFixture",
    "Cluckwork.Infrastructure.Modules.Access.Identity.AccessModule")]
internal static class AccessModuleRules { }
