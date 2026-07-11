namespace Cluckwork.Api.Endpoints.DailyEntries;

using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;

public static class DailyEntryEndpoints
{
    public static RouteGroupBuilder MapDailyEntryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/", RecordDailyEntry)
            .WithName("RecordDailyEntry")
            .WithSummary("Record or update the daily production entry for a flock/house.");

        return group;
    }

    private static async Task<IResult> RecordDailyEntry(
        RecordDailyEntryRequest request,
        RecordDailyEntryHandler handler,
        IValidator<RecordDailyEntryCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved)
            return Results.Unauthorized();

        var command = new RecordDailyEntryCommand(
            request.FarmId, request.HouseId, request.FlockId, request.Date,
            request.TotalEggs, request.CrackedEggs, request.DirtyEggs,
            request.DiscardedEggs, request.MortalityCount);

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);

        return result.IsSuccess
            ? Results.Created($"/api/v1/daily-entries/{result.Value}", new { Id = result.Value })
            : Results.Problem(result.Error.Description, statusCode: 422, title: result.Error.Code);
    }
}

public sealed record RecordDailyEntryRequest(
    Guid FarmId, Guid HouseId, Guid FlockId, DateOnly Date,
    int TotalEggs, int CrackedEggs, int DirtyEggs, int DiscardedEggs, int MortalityCount);
