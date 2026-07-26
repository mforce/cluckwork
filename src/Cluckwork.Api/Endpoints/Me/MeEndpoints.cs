namespace Cluckwork.Api.Endpoints.Me;

using Cluckwork.Api.Validation;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users.SetLanguage;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;

// #45 — the user-scoped counterpart to the farm-scoped /account group. Identity
// comes from the JWT (sub), not the body. Mounted on the DEFAULT auth policy so
// every role, ReadOnly included, can read their identity and set their language.
public static class MeEndpoints
{
    public static RouteGroupBuilder MapMeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", GetMe)
            .WithName("GetMe")
            .WithSummary("The signed-in user's identity and UI-language preference.");

        // #45 — self-service, EVERY role incl. ReadOnly. No named write policy:
        // a UI-language preference is personal, not a farm-scoped admin action.
        group.MapPut("/language", SetLanguage)
            .WithName("SetOwnLanguage")
            .WithSummary("Set or clear your UI-language preference.");

        return group;
    }

    private static async Task<IResult> GetMe(
        IIdentityProvider identity, ICurrentUser currentUser, TenantContext tenant,
        CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved) return Results.Unauthorized();
        var profile = await identity.GetUserAsync(tenant.AccountId, currentUser.UserId, ct);
        return profile is null
            ? Results.NotFound()
            : Results.Ok(new MeResponse(
                profile.Id, profile.Email, profile.DisplayName, profile.Role, profile.Language));
    }

    private static async Task<IResult> SetLanguage(
        SetLanguageRequest request, SetLanguageHandler handler,
        IValidator<SetLanguageCommand> validator, ICurrentUser currentUser,
        TenantContext tenant, CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved) return Results.Unauthorized();

        // Canonical form: trim + lowercase. null stays null (clears); "  " collapses
        // to "" and is rejected as invalid below, never treated as null.
        var language = request.Language?.Trim().ToLowerInvariant();
        var command = new SetLanguageCommand(language);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation); // carries Me.Language.Format

        var result = await handler.HandleAsync(command, tenant.AccountId, currentUser.UserId, ct);
        return result.IsSuccess ? Results.NoContent() : MapFailure(result.Error);
    }

    private static IResult MapFailure(Error error) =>
        error.Code.EndsWith(".NotFound", StringComparison.Ordinal)
            ? Results.NotFound()
            : Results.Problem(error.Description, statusCode: 422, title: error.Code);
}

// Field order is part of the contract the SPA (#182) parses. `Name` is DisplayName
// so the SPA needn't decode the JWT for it; role is echoed (JWT stays authoritative).
public sealed record MeResponse(Guid Id, string Email, string? Name, string Role, string? Language);

public sealed record SetLanguageRequest
{
    // `required` so an ABSENT property is a 400 (malformed body): PUT sets one
    // absolute preference, so the field must be present. An explicit null clears
    // it; a missing field is a client bug, not a clear.
    public required string? Language { get; init; }
}
