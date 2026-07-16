namespace Cluckwork.Api.Endpoints.EggGrades;

using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;

public static class EggGradeEndpoints
{
    public static RouteGroupBuilder MapEggGradeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListEggGrades)
            .WithName("ListEggGrades")
            .WithSummary("List the account's active egg grades (daily-entry grade lines reference these).");

        return group;
    }

    private static async Task<IResult> ListEggGrades(
        IEggGradeRepository grades, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var list = await grades.ListActiveAsync(ct);
        return Results.Ok(list.Select(g => new EggGradeResponse(
            g.Id, g.Name, g.GradeType.ToString(), g.SortOrder, g.IsSaleable)));
    }
}

public sealed record EggGradeResponse(
    Guid Id, string Name, string GradeType, int SortOrder, bool IsSaleable);
