namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

public sealed class AccountProvisioner(
    AppDbContext db,
    TenantContext tenant,
    CurrentUserContext currentUser,
    IAccountUserDirectory users,
    IIdentityProvider identity,
    IAuditWriter audit)
{
    public async Task<Result<AccountProvisionOutcome>> ProvisionAsync(
        string? name,
        string? slug,
        string? ownerEmail,
        string? locale = null,
        string? currencyCode = null,
        CancellationToken ct = default)
    {
        var validated = Validate(name, slug, ownerEmail, locale, currencyCode);
        if (validated.IsFailure)
            return Result.Failure<AccountProvisionOutcome>(validated.Error);

        var input = validated.Value;
        var existing = await db.Accounts.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(account => account.Slug == input.Slug, ct);
        if (existing is not null)
            return await ExistingSlugFailureAsync(existing, input.OwnerEmail, ct);

        return await ProvisionValidatedAsync(Guid.NewGuid(), input, ct);
    }

    internal async Task<Result<AccountProvisionOutcome>> ProvisionSkippingSlugPrecheckForTestAsync(
        Guid accountId,
        string? name,
        string? slug,
        string? ownerEmail,
        string? locale = null,
        string? currencyCode = null,
        CancellationToken ct = default)
    {
        var validated = Validate(name, slug, ownerEmail, locale, currencyCode);
        return validated.IsFailure
            ? Result.Failure<AccountProvisionOutcome>(validated.Error)
            : await ProvisionValidatedAsync(accountId, validated.Value, ct);
    }

    private async Task<Result<AccountProvisionOutcome>> ExistingSlugFailureAsync(
        Account account, string ownerEmail, CancellationToken ct)
    {
        var candidate = await users.FindByAccountEmailAsync(account.Id, ownerEmail, ct);
        if (candidate is null)
            return SlugTaken(account.Slug);

        var owners = await users.FindByAccountRoleAsync(account.Id, Roles.Owner, ct);
        if (!owners.Any(owner => owner.Id == candidate.Id))
            return SlugTaken(account.Slug);

        if (!account.IsActive)
            return Result.Failure<AccountProvisionOutcome>(Error.Conflict(
                "Provision.SlugTakenSuspended",
                $"Farm code '{account.Slug}' is already committed but the farm is suspended. " +
                $"Run reactivate-account --slug {account.Slug} before recovering its Owner."));

        if (candidate.DisabledAt is not null)
            return Result.Failure<AccountProvisionOutcome>(Error.Conflict(
                "Provision.SlugTakenOwnerDisabled",
                $"Farm code '{account.Slug}' is already committed, but its matching Owner is disabled."));

        return Result.Failure<AccountProvisionOutcome>(Error.Conflict(
            "Provision.SlugTakenRecoverable",
            $"Farm code '{account.Slug}' is already committed with this Owner. The one-time password may " +
            $"have been lost after commit; run recover-admin --email {ownerEmail} --account {account.Id} " +
            "--reason <reason>."));
    }

    private async Task<Result<AccountProvisionOutcome>> ProvisionValidatedAsync(
        Guid accountId, ProvisionInput input, CancellationToken ct)
    {
        tenant.Resolve(accountId);
        currentUser.ResolveSystemActor(SystemActors.ProvisionAccount);

        try
        {
            return await AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
            {
                var account = Account.Create(
                    accountId, input.Name, input.Slug, "UTC",
                    input.CurrencyCode, input.Locale);
                db.Accounts.Add(account);
                await db.SaveChangesAsync(token);

                db.EggGrades.AddRange(EggGrade.Defaults(accountId, SeedDefaults.FarmId));
                db.EggUnitConversions.AddRange(EggUnitConversion.Defaults(accountId));
                await audit.WriteAsync(
                    AuditActions.AccountProvisioned, "Account", accountId,
                    details: new { input.Slug }, ct: token);

                var password = TemporaryPassword.Generate();
                var created = await identity.CreateUserAsync(
                    accountId, input.OwnerEmail, password, Roles.Owner,
                    name: "Administrator", mustChangePassword: true, ct: token);
                if (created.IsFailure)
                {
                    await transaction.RollbackAsync(token);
                    return Result.Failure<AccountProvisionOutcome>(created.Error);
                }

                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return Result.Success(new AccountProvisionOutcome(
                    accountId, input.Slug, input.OwnerEmail, password));
            }, ct);
        }
        catch (DbUpdateException ex) when (IsSlugConflict(ex))
        {
            return SlugTaken(input.Slug);
        }
    }

    internal static bool IsSlugConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_Accounts_Slug",
        };

    private static Result<ProvisionInput> Validate(
        string? name,
        string? slug,
        string? ownerEmail,
        string? locale,
        string? currencyCode)
    {
        var normalizedName = name?.Trim() ?? string.Empty;
        if (normalizedName.Length == 0)
            return Result.Failure<ProvisionInput>(Error.Validation(
                "Provision.NameRequired", "A farm name is required."));
        if (normalizedName.Length > Account.MaxNameLength)
            return Result.Failure<ProvisionInput>(Error.Validation(
                "Provision.NameTooLong", $"Farm name cannot exceed {Account.MaxNameLength} characters."));

        var normalizedEmail = ownerEmail?.Trim() ?? string.Empty;
        if (normalizedEmail.Length == 0)
            return Result.Failure<ProvisionInput>(Error.Validation(
                "Provision.EmailRequired", "An --owner-email is required."));

        var normalizedSlug = Account.TryValidateSlug(slug);
        if (normalizedSlug.IsFailure)
            return Result.Failure<ProvisionInput>(normalizedSlug.Error);

        var normalizedLocale = (locale ?? Account.DefaultLocale).Trim();
        if (!FarmSettingsRules.IsSpecificCulture(normalizedLocale))
            return Result.Failure<ProvisionInput>(Error.Validation(
                "Provision.LocaleInvalid", $"'{locale}' is not a specific locale such as en-US."));

        var normalizedCurrency = (currencyCode ?? "USD").Trim().ToUpperInvariant();
        if (!CurrencyCatalog.IsWellFormedCode(normalizedCurrency))
            return Result.Failure<ProvisionInput>(Error.Validation(
                "Provision.CurrencyInvalid", "Currency must be a three-letter ISO 4217 code."));

        return Result.Success(new ProvisionInput(
            normalizedName, normalizedSlug.Value, normalizedEmail,
            normalizedLocale, normalizedCurrency));
    }

    private static Result<AccountProvisionOutcome> SlugTaken(string slug) =>
        Result.Failure<AccountProvisionOutcome>(Error.Conflict(
            "Provision.SlugTaken", $"Farm code '{slug}' is already taken."));

    private sealed record ProvisionInput(
        string Name,
        string Slug,
        string OwnerEmail,
        string Locale,
        string CurrencyCode);
}

public sealed record AccountProvisionOutcome(
    Guid AccountId,
    string Slug,
    string OwnerEmail,
    string TemporaryPassword);
