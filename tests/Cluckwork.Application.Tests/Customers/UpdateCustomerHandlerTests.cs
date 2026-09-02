namespace Cluckwork.Application.Tests.Customers;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Customers;
using Cluckwork.Application.Features.Customers.UpdateCustomer;
using Cluckwork.Domain.Sales;

public sealed class UpdateCustomerHandlerTests
{
    private sealed class FakeCustomerRepository : ICustomerRepository
    {
        private readonly Dictionary<Guid, Customer> _store = new();

        public void Seed(Customer customer) => _store[customer.Id] = customer;

        // #512 — the fake satisfies the interface for this handler's needs; the
        // bulk display-name read is exercised by the integration suite, where a
        // real tenant filter exists to test against.
        public Task<IReadOnlyDictionary<Guid, CustomerReference>> GetDisplayNamesAsync(
            IReadOnlyCollection<Guid> customerIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CustomerReference>>(
                _store.Values.Where(c => customerIds.Contains(c.Id))
                    .ToDictionary(c => c.Id, c => new CustomerReference(c.Id, c.Name)));

        public Task<Customer?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_store.TryGetValue(id, out var c) ? c : null);

        public Task<IReadOnlyList<Customer>> ListAsync(int limit, int offset, CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<Customer>)_store.Values.ToList());

        // #512 interface addition; this fixture exercises the update handler
        // only, so discovery is not part of what this fake is asked to do.
        public Task<IReadOnlyList<Customer>> SearchAsync(
            string? search, int limit, int offset, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task AddAsync(Customer entity, CancellationToken ct = default)
        {
            _store[entity.Id] = entity;
            return Task.CompletedTask;
        }

        public void Update(Customer entity) { }
        public void Remove(Customer entity) { }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int SaveChangesCallCount { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            SaveChangesCallCount++;
            return Task.FromResult(1);
        }

        public async Task<bool> ExecuteInTransactionAsync(
            Func<CancellationToken, Task<bool>> operation, CancellationToken ct = default) =>
            await operation(ct);
    }

    private sealed record AuditCall(string Action, string EntityType, Guid EntityId);

    private sealed class FakeAuditWriter : IAuditWriter
    {
        public List<AuditCall> Calls { get; } = new();

        public Task WriteAsync(
            string action, string entityType, Guid entityId,
            string? reason = null, object? details = null, CancellationToken ct = default)
        {
            Calls.Add(new AuditCall(action, entityType, entityId));
            return Task.CompletedTask;
        }
    }

    private static Customer MakeCustomer() =>
        Customer.Create(Guid.NewGuid(), Guid.NewGuid(), "Original Name", "555-0000");

    // #625 review round 6 — overloads, not `version ?? c.Version`: that
    // default made an explicitly-passed `null` indistinguishable from "use
    // the row's current Version", so a null-Version handler test could not
    // actually be expressed. The zero-arg overload is "use current Version"
    // (every pre-existing call site); the explicit overload sends EXACTLY
    // the value given, null included.
    private static UpdateCustomerCommand CommandFor(Customer c) => new(
        c.Id, c.Version, "New Name", "555-1111", "new@example.com", "New Addr", "New Note");

    private static UpdateCustomerCommand CommandFor(Customer c, int? version) => new(
        c.Id, version, "New Name", "555-1111", "new@example.com", "New Addr", "New Note");

    [Fact]
    public async Task UnknownCustomer_ReturnsNotFound()
    {
        var repo = new FakeCustomerRepository();
        var uow = new FakeUnitOfWork();
        var audit = new FakeAuditWriter();
        var handler = new UpdateCustomerHandler(repo, uow, audit);

        var command = new UpdateCustomerCommand(
            Guid.NewGuid(), 0, "New Name", "555-1111", null, null, null);
        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Customer.NotFound", result.Error.Code);
        Assert.Equal(0, uow.SaveChangesCallCount);
        Assert.Empty(audit.Calls);
    }

    [Fact]
    public async Task VersionMismatch_ReturnsConflict_LeavesStateAndAuditUnchanged()
    {
        var repo = new FakeCustomerRepository();
        var uow = new FakeUnitOfWork();
        var audit = new FakeAuditWriter();
        var customer = MakeCustomer();
        repo.Seed(customer);
        var handler = new UpdateCustomerHandler(repo, uow, audit);

        var command = CommandFor(customer, version: customer.Version + 1);
        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Customer.VersionMismatch", result.Error.Code);
        Assert.Equal("Original Name", customer.Name);
        Assert.Equal(0, customer.Version);
        Assert.Equal(0, uow.SaveChangesCallCount);
        Assert.Empty(audit.Calls);
    }

    [Fact]
    public async Task LowerSubmittedVersion_ReturnsConflict_LeavesCommittedStateAndAuditUnchanged()
    {
        var repo = new FakeCustomerRepository();
        var uow = new FakeUnitOfWork();
        var audit = new FakeAuditWriter();
        var customer = MakeCustomer();
        Assert.True(customer.Update("Committed Name", "555-2222", "committed@example.com", "Committed Addr", "Committed Note").IsSuccess);
        Assert.Equal(1, customer.Version);
        repo.Seed(customer);
        var handler = new UpdateCustomerHandler(repo, uow, audit);

        var result = await handler.HandleAsync(CommandFor(customer, version: 0), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Customer.VersionMismatch", result.Error.Code);
        Assert.Equal("Committed Name", customer.Name);
        Assert.Equal("555-2222", customer.Phone);
        Assert.Equal("committed@example.com", customer.Email);
        Assert.Equal("Committed Addr", customer.Address);
        Assert.Equal("Committed Note", customer.Note);
        Assert.Equal(1, customer.Version);
        Assert.Equal(0, uow.SaveChangesCallCount);
        Assert.Empty(audit.Calls);
    }

    // #625 review round 6 — the validator normally rejects a null Version
    // before the handler ever runs, but the handler's own guard is what
    // actually stops a mutation — this proves that defense directly,
    // bypassing the validator layer entirely (a fake repository/unit-of-
    // work/audit-writer, exactly like the other handler tests here).
    [Fact]
    public async Task NullVersion_ReturnsConflict_LeavesStateAndAuditUnchanged()
    {
        var repo = new FakeCustomerRepository();
        var uow = new FakeUnitOfWork();
        var audit = new FakeAuditWriter();
        var customer = MakeCustomer();
        repo.Seed(customer);
        var handler = new UpdateCustomerHandler(repo, uow, audit);

        var command = CommandFor(customer, version: null);
        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Customer.VersionMismatch", result.Error.Code);
        Assert.Equal("Original Name", customer.Name);
        Assert.Equal(0, customer.Version);
        Assert.Equal(0, uow.SaveChangesCallCount);
        Assert.Empty(audit.Calls);
    }

    [Fact]
    public async Task ValidUpdate_Saves_EmitsCustomerUpdateAudit()
    {
        var repo = new FakeCustomerRepository();
        var uow = new FakeUnitOfWork();
        var audit = new FakeAuditWriter();
        var customer = MakeCustomer();
        repo.Seed(customer);
        var handler = new UpdateCustomerHandler(repo, uow, audit);

        var command = CommandFor(customer);
        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("New Name", customer.Name);
        Assert.Equal(1, customer.Version);
        Assert.Equal(1, uow.SaveChangesCallCount);
        var call = Assert.Single(audit.Calls);
        Assert.Equal(AuditActions.CustomerUpdate, call.Action);
        Assert.Equal("Customer.Update", call.Action);
        Assert.Equal(nameof(Customer), call.EntityType);
        Assert.Equal(customer.Id, call.EntityId);
    }
}
