namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

// #532 — the ORDER guard. AddIdentityCore registers the stock validator with
// TryAddScoped, so registering ours BEFORE it makes that a no-op. Registering
// ours AFTER leaves BOTH, and the stock one's global FindByNameAsync then
// rejects the second farm's duplicate email before Postgres sees it — with no
// error anywhere that names the cause.
//
// Asserting the COUNT is what catches that: a type-only assertion passes with
// two validators registered.
[Collection(IntegrationCollection.Name)]
public sealed class AccountScopedUserValidatorRegistrationTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public void ExactlyOneUserValidator_IsRegistered_AndItIsTheAccountScopedOne()
    {
        using var scope = factory.Services.CreateScope();

        var validators = scope.ServiceProvider
            .GetServices<IUserValidator<ApplicationUser>>()
            .ToList();

        Assert.Single(validators);
        Assert.IsType<AccountScopedUserValidator>(validators[0]);
    }
}
