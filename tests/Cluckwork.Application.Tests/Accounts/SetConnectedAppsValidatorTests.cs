using Cluckwork.Application.Modules.Farm.Accounts.SetConnectedApps;
using Cluckwork.Application.Modules.Farm.Contracts;

namespace Cluckwork.Application.Tests.Accounts;

// #1146 — a client that omits the switch must not turn connected apps off.
public sealed class SetConnectedAppsValidatorTests
{
    private readonly SetConnectedAppsValidator _validator = new();

    [Fact]
    public void BothValues_Pass()
    {
        Assert.True(_validator.Validate(new SetConnectedAppsCommand(true, 0)).IsValid);
        Assert.True(_validator.Validate(new SetConnectedAppsCommand(false, 3)).IsValid);
    }

    [Fact]
    public void MissingSwitch_Fails() =>
        Assert.Contains(_validator.Validate(new SetConnectedAppsCommand(null, 0)).Errors,
            error => error.PropertyName == nameof(SetConnectedAppsCommand.Allow));
}
