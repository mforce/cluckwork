namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;

// #673 — tenant isolation's two write-side layers both key on AccountId being a
// non-nullable Guid, and both used to walk PAST any other shape: no concurrency
// token from the #562 model walk, no check in TenantStampInterceptor. That is
// the #562 hole reopened by a single mapping, with every test green.
//
// These tests prove each layer now refuses that shape independently, against a
// throwaway entity: the mapping under test cannot exist in AppDbContext's own
// model, because the walk it must fail is the one AppDbContext runs. No
// database — both refusals happen before any SQL is sent.
public sealed class AccountIdShapeFailClosedTests
{
    private const string ModelOnly = "Host=localhost;Database=model-only;Username=none;Password=none";

    private sealed class NullableAccountIdEntity
    {
        public Guid Id { get; set; }
        public Guid? AccountId { get; set; }
    }

    private sealed class ConvertedAccountIdEntity
    {
        public Guid Id { get; set; }
        public AccountKey AccountId { get; set; }
    }

    private sealed class PlainAccountIdEntity
    {
        public Guid Id { get; set; }
        public Guid AccountId { get; set; }
    }

    private readonly record struct AccountKey(Guid Value);

    // One probe context PER SHAPE, and that is load-bearing: EF caches the
    // built model by context TYPE, so a single parameterised probe would hand
    // every test whichever model happened to be built first — two of these
    // tests passed vacuously that way while being written.
    private sealed class NullableProbeContext(DbContextOptions<NullableProbeContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<NullableAccountIdEntity>();
            AppDbContext.ApplyAccountIdTenantTokens(builder);
        }
    }

    private sealed class ConvertedProbeContext(DbContextOptions<ConvertedProbeContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<ConvertedAccountIdEntity>()
                .Property(e => e.AccountId)
                .HasConversion(key => key.Value, value => new AccountKey(value));
            AppDbContext.ApplyAccountIdTenantTokens(builder);
        }
    }

    private sealed class PlainProbeContext(DbContextOptions<PlainProbeContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<PlainAccountIdEntity>();
            AppDbContext.ApplyAccountIdTenantTokens(builder);
        }
    }

    // Maps `Guid? AccountId` WITHOUT the walk, which is the point: the
    // interceptor has to refuse the write on its own, not lean on a model that
    // would have failed to build.
    private sealed class WriteProbeContext(DbContextOptions<WriteProbeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder) =>
            builder.Entity<NullableAccountIdEntity>();
    }

    private static TContext Probe<TContext>() where TContext : DbContext =>
        (TContext)Activator.CreateInstance(
            typeof(TContext),
            new DbContextOptionsBuilder<TContext>().UseNpgsql(ModelOnly).Options)!;

    private static WriteProbeContext Writes(out TenantContext tenant)
    {
        var resolved = new TenantContext();
        resolved.Resolve(Guid.NewGuid());
        tenant = resolved;
        return new WriteProbeContext(new DbContextOptionsBuilder<WriteProbeContext>()
            .UseNpgsql(ModelOnly)
            .AddInterceptors(new TenantStampInterceptor(resolved))
            .Options);
    }

    [Fact]
    public void ModelWalk_RefusesANullableAccountId()
    {
        using var db = Probe<NullableProbeContext>();

        var ex = Assert.Throws<TenantAccountIdShapeException>(() => _ = db.Model);

        Assert.Equal(nameof(NullableAccountIdEntity), ex.EntityType);
        Assert.Contains("non-nullable Guid", ex.Message, StringComparison.Ordinal);
    }

    // A strongly-typed id is the other way the same hole opens: the property's
    // CLR type is the wrapper, so the walk's `!= typeof(Guid)` skipped it even
    // though the column is a uuid.
    [Fact]
    public void ModelWalk_RefusesAConvertedAccountId()
    {
        using var db = Probe<ConvertedProbeContext>();

        var ex = Assert.Throws<TenantAccountIdShapeException>(() => _ = db.Model);

        Assert.Equal(nameof(ConvertedAccountIdEntity), ex.EntityType);
    }

    // Must-still-pass control: the walk keeps doing its #562 job for the shape
    // it accepts. Without this, deleting the whole walk would still pass the two
    // refusal tests above — they would just fail for a different reason.
    [Fact]
    public void ModelWalk_StillTokensAPlainGuidAccountId()
    {
        using var db = Probe<PlainProbeContext>();

        var accountId = db.Model.FindEntityType(typeof(PlainAccountIdEntity))!.FindProperty("AccountId")!;

        Assert.Equal(typeof(Guid), accountId.ClrType);
        Assert.True(accountId.IsConcurrencyToken);
    }

    // The insert that used to slip through unstamped: a null AccountId is not a
    // Guid, so the old `is not Guid ... return` left the row with no tenant at
    // all — and every later write to it unverified for the same reason.
    [Fact]
    public void Interceptor_RefusesAnUnstampableInsert()
    {
        using var db = Writes(out _);
        db.Add(new NullableAccountIdEntity { Id = Guid.NewGuid(), AccountId = null });

        var ex = Assert.Throws<TenantAccountIdShapeException>(() => db.SaveChanges());

        Assert.Equal(nameof(NullableAccountIdEntity), ex.EntityType);
        Assert.Contains("Added", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Interceptor_RefusesAnUncheckableUpdate()
    {
        using var db = Writes(out _);
        var stub = new NullableAccountIdEntity { Id = Guid.NewGuid(), AccountId = null };
        db.Attach(stub);
        db.Entry(stub).State = EntityState.Modified;

        var ex = Assert.Throws<TenantAccountIdShapeException>(() => db.SaveChanges());

        Assert.Contains("Modified", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Interceptor_RefusesAnUncheckableDelete()
    {
        using var db = Writes(out _);
        var stub = new NullableAccountIdEntity { Id = Guid.NewGuid(), AccountId = null };
        db.Attach(stub);
        db.Remove(stub);

        var ex = Assert.Throws<TenantAccountIdShapeException>(() => db.SaveChanges());

        Assert.Contains("Deleted", ex.Message, StringComparison.Ordinal);
    }

    // The refusal is scoped to a resolved tenant exactly as every other check in
    // the interceptor is: the CLI verbs, the seeders' pre-checks and
    // AppDbContextDesignTimeFactory all save with no tenant by design.
    [Fact]
    public void Interceptor_LeavesAnUnresolvedTenantAlone()
    {
        var unresolved = new TenantContext();
        using var db = new WriteProbeContext(new DbContextOptionsBuilder<WriteProbeContext>()
            .UseNpgsql(ModelOnly)
            .AddInterceptors(new TenantStampInterceptor(unresolved))
            .Options);
        db.Add(new NullableAccountIdEntity { Id = Guid.NewGuid(), AccountId = null });

        // No TenantAccountIdShapeException: the save gets as far as the database
        // and fails there, on the connection this test deliberately cannot open.
        Assert.IsNotType<TenantAccountIdShapeException>(Record.Exception(() => db.SaveChanges()));
    }
}

