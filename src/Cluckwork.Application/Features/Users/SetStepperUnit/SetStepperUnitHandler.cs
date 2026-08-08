namespace Cluckwork.Application.Features.Users.SetStepperUnit;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Catalog;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;

// The user id comes from the token (the endpoint), never the body: a caller can
// only ever set their OWN stepper-unit preference. Account-scoped inside the
// provider, same as SetLanguageHandler.
public sealed class SetStepperUnitHandler(
    IIdentityProvider identity, IEggUnitConversionRepository conversions)
{
    public async Task<Result> HandleAsync(
        SetStepperUnitCommand command, Guid accountId, Guid userId, CancellationToken ct = default)
    {
        if (command.Unit is null)
            return await identity.SetStepperUnitAsync(accountId, userId, null, ct);

        var unit = Enum.Parse<EggUnit>(command.Unit, ignoreCase: true);

        // A farm may deactivate a conversion (or never activate one beyond the
        // seeded defaults) — a preference pointing at one that isn't there (or
        // isn't active) would silently fall back to +1/-1 with no explanation,
        // same failure this guards against on the sales-order line (AddOrderItemHandler).
        var conversion = await conversions.GetByUnitAsync(unit, ct);
        if (conversion is null || !conversion.Active)
            return Result.Failure(Error.Validation(
                "Me.StepperUnit.NoUnitConversion",
                $"No active eggs-per-unit definition for '{unit}' — set one on the Products screen."));

        return await identity.SetStepperUnitAsync(accountId, userId, unit, ct);
    }
}
