namespace Cluckwork.Api.Endpoints.Accounts;

using Cluckwork.Application.Features.Accounts;
using Cluckwork.Infrastructure.Persistence;

public static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccountEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", GetAccount)
            .WithName("GetAccount")
            .WithSummary("Current account: name and default currency. Clients need the currency to parse/format money input correctly (JPY has 0 decimals).");
        return group;
    }

    private static async Task<IResult> GetAccount(
        IAccountRepository accounts, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var account = await accounts.GetCurrentAsync(ct);
        return account is null
            ? Results.NotFound()
            : Results.Ok(new AccountResponse(
                account.Id, account.Name,
                account.DefaultCurrencyCode, account.DefaultCurrencyMinorUnit));
    }
}

public sealed record AccountResponse(
    Guid Id, string Name, string CurrencyCode, int CurrencyMinorUnit);
