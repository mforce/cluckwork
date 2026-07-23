namespace Cluckwork.Api.Endpoints.Customers;

using Cluckwork.Application.Features.Customers;
using Cluckwork.Application.Features.Customers.CreateCustomer;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;

public static class CustomerEndpoints
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;

    public static RouteGroupBuilder MapCustomerEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/", CreateCustomer)
            .WithName("CreateCustomer")
            .WithSummary("Create a customer (name + phone required; email/address/note optional).")
            .RequireAuthorization(AuthPolicies.SalesFlow);

        // Customer directory carries PII (name/phone/email/address). Gate the
        // reads to the sell-flow tier — workers build orders, ReadOnly is fenced
        // out — matching the writes above and the money reads on /payments (#127).
        group.MapGet("/", ListCustomers)
            .WithName("ListCustomers")
            .WithSummary("List the account's customers by name (paged).")
            .RequireAuthorization(AuthPolicies.SalesFlow);

        group.MapGet("/{id:guid}", GetCustomer)
            .WithName("GetCustomer")
            .WithSummary("Get a single customer by id.")
            .RequireAuthorization(AuthPolicies.SalesFlow);

        return group;
    }

    private static async Task<IResult> CreateCustomer(
        CreateCustomerRequest request,
        CreateCustomerHandler handler,
        IValidator<CreateCustomerCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new CreateCustomerCommand(
            request.Name, request.Phone, request.Email, request.Address, request.Note);

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess
            ? Results.Created($"/api/v1/customers/{result.Value}", new { Id = result.Value })
            : Results.Problem(result.Error.Description, statusCode: 422, title: result.Error.Code);
    }

    private static async Task<IResult> ListCustomers(
        ICustomerRepository customers, TenantContext tenant, CancellationToken ct,
        int? limit = null, int? offset = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);

        var list = await customers.ListAsync(take, skip, ct);
        return Results.Ok(list.Select(ToResponse));
    }

    private static async Task<IResult> GetCustomer(
        Guid id, ICustomerRepository customers, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var customer = await customers.GetByIdAsync(id, ct);
        return customer is null ? Results.NotFound() : Results.Ok(ToResponse(customer));
    }

    private static CustomerResponse ToResponse(Customer c) =>
        new(c.Id, c.Name, c.Phone, c.Email, c.Address, c.Note);
}

public sealed record CreateCustomerRequest(
    string Name, string Phone, string? Email = null, string? Address = null, string? Note = null);

public sealed record CustomerResponse(
    Guid Id, string Name, string Phone, string? Email, string? Address, string? Note);
