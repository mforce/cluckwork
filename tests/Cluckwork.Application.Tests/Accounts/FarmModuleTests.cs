using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Modules.Commerce.Contracts;

namespace Cluckwork.Application.Tests.Accounts;

// #851: the settings read copies the account field by field into a positional
// record, where two strings or two enums can swap without a compile error.
public sealed class FarmModuleTests
{
    private static readonly Guid AccountId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static Account Account()
    {
        var account = Cluckwork.Domain.Accounts.Account.Create(
            AccountId, "Hilltop Farm", "hilltop", "Asia/Kuwait", "USD", "es");
        var updated = account.UpdateSettings(
            "Hilltop Farm", "Asia/Kuwait", "es", "KWD", UnitSystem.Imperial, DayOfWeek.Saturday,
            "dd/MM/yyyy", "HH:mm", "forest", EggUnit.Tray, WorkerSaleAllocationPolicy.AllFarmFlocks,
            maxDiscountBasisPoints: 1_250, financialRowsExist: false);
        Assert.True(updated.IsSuccess);
        return account;
    }

    // The handlers and the other ports sit on paths these tests do not exercise.
    private static FarmModule Module(Account? account) =>
        new(new StubAccounts(account), null!, null!, null!, null!, null!, null!, null!);

    [Fact]
    public async Task GetSettings_CopiesEveryField()
    {
        var account = Account();
        // The symbol comes from ICU, which differs across hosts, so it is the one
        // value read back from the account rather than pinned.
        Assert.Equal(
            new FarmSettingsDetails(
                AccountId, "Hilltop Farm", "KWD", 3, account.CurrencySymbol, "Asia/Kuwait", "es",
                UnitSystem.Imperial, DayOfWeek.Saturday, "dd/MM/yyyy", "HH:mm", "forest",
                EggUnit.Tray, WorkerSaleAllocationPolicy.AllFarmFlocks, 12.5m, 1),
            await Module(account).GetSettingsAsync(default));
    }

    [Fact]
    public async Task GetSettings_NoCurrentAccountIsNull() =>
        Assert.Null(await Module(null).GetSettingsAsync(default));

    private sealed class StubAccounts(Account? account) : IAccountRepository
    {
        public Task<Account?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(account);

        public Task<Account?> GetCurrentTrackedAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Account?> GetCurrentSharedLockedAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Account?> GetCurrentLockedAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Account?> FindBySlugAsync(string slug, CancellationToken ct = default) => throw new NotSupportedException();
        public void DiscardChanges(Account account) => throw new NotSupportedException();
    }
}
