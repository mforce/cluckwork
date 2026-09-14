// #847 (epic #514 slice 5) — seam-surface semantics against fixture interfaces, one assertion per rule.

// One sub-namespace per scenario, so a test scans exactly the fixture it names
// and never sees another fixture's violation.

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.QueryableReturn
{
    using Cluckwork.Domain.Flocks;

    public interface IQueryableReturnFixture
    {
        IQueryable<Flock> Query();
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.DbSetReturn
{
    using Cluckwork.Domain.Expenses;
    using Microsoft.EntityFrameworkCore;

    public interface IDbSetReturnFixture
    {
        DbSet<Expense> Query();
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.AppDbContextParameter
{
    using Cluckwork.Infrastructure.Persistence;

    public interface IAppDbContextParameterFixture
    {
        void Handle(AppDbContext db);
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.AggregateRootReturn
{
    using Cluckwork.Domain.Common;

    public interface IAggregateRootReturnFixture
    {
        AggregateRoot<Guid> Get();
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.EntityParameter
{
    using Cluckwork.Domain.Common;

    public interface IEntityParameterFixture
    {
        void Handle(Entity<Guid> entity);
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.EfMetadataNested
{
    using Microsoft.EntityFrameworkCore.Metadata;

    public interface IEfMetadataNestedFixture
    {
        Task<IReadOnlyList<IEntityType>> Get();
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.DtoWithQueryableProperty
{
    using Cluckwork.Domain.Flocks;

    public sealed class DtoWithQueryableProperty
    {
        public IQueryable<Flock>? Items { get; init; }
    }

    public interface IDtoWithQueryablePropertyFixture
    {
        DtoWithQueryableProperty Get();
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.ConcreteAggregateReturn
{
    using Cluckwork.Domain.Flocks;

    public interface IConcreteAggregateReturnFixture
    {
        Task<IReadOnlyList<Flock>> List();
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.PagedResultReturn
{
    using Cluckwork.Application.Common;
    using Cluckwork.Domain.Flocks;

    public interface IPagedResultReturnFixture
    {
        Task<PagedResult<Flock>> Page();
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.MoneyReturn
{
    using Cluckwork.Domain.Common;

    public interface IMoneyReturnFixture
    {
        Task<Money> Total();
    }
}

namespace Cluckwork.Application.Tests.Architecture
{
    using System.Reflection;
    using Cluckwork.Application.Common;
    using QueryableReturnFixtures = SeamFixtures.QueryableReturn;
    using DbSetReturnFixtures = SeamFixtures.DbSetReturn;
    using AppDbContextParameterFixtures = SeamFixtures.AppDbContextParameter;
    using AggregateRootReturnFixtures = SeamFixtures.AggregateRootReturn;
    using EntityParameterFixtures = SeamFixtures.EntityParameter;
    using EfMetadataNestedFixtures = SeamFixtures.EfMetadataNested;
    using DtoWithQueryablePropertyFixtures = SeamFixtures.DtoWithQueryableProperty;
    using ConcreteAggregateReturnFixtures = SeamFixtures.ConcreteAggregateReturn;
    using PagedResultReturnFixtures = SeamFixtures.PagedResultReturn;
    using MoneyReturnFixtures = SeamFixtures.MoneyReturn;

    public sealed class SeamSurfaceTests
    {
        private static SeamSurfaceReport Scan<T>(int floor = 1) =>
            SeamSurfaceScanner.Scan(typeof(T).Assembly, [typeof(T).Namespace!], floor);

        private static IReadOnlyList<string> Evaluate<T>(int floor = 1) =>
            SeamSurfaceScanner.Evaluate(Scan<T>(floor));

        [Fact]
        public void QueryableReturn_IsAViolation()
        {
            var failure = Assert.Single(Evaluate<QueryableReturnFixtures.IQueryableReturnFixture>());
            Assert.Contains("IQueryableReturnFixture.Query", failure);
            Assert.Contains("IQueryable", failure);
        }

        [Fact]
        public void DbSetReturn_IsAViolation()
        {
            var failure = Assert.Single(Evaluate<DbSetReturnFixtures.IDbSetReturnFixture>());
            Assert.Contains("IDbSetReturnFixture.Query", failure);
            Assert.Contains("DbSet", failure);
        }

        [Fact]
        public void AppDbContextParameter_IsAViolation()
        {
            var failure = Assert.Single(Evaluate<AppDbContextParameterFixtures.IAppDbContextParameterFixture>());
            Assert.Contains("IAppDbContextParameterFixture.Handle", failure);
            Assert.Contains("AppDbContext", failure);
        }

        [Fact]
        public void AggregateRootBaseTypeReturn_IsAViolation()
        {
            var failure = Assert.Single(Evaluate<AggregateRootReturnFixtures.IAggregateRootReturnFixture>());
            Assert.Contains("IAggregateRootReturnFixture.Get", failure);
            Assert.Contains("AggregateRoot", failure);
        }

        [Fact]
        public void EntityBaseTypeParameter_IsAViolation()
        {
            var failure = Assert.Single(Evaluate<EntityParameterFixtures.IEntityParameterFixture>());
            Assert.Contains("IEntityParameterFixture.Handle", failure);
            Assert.Contains("Entity", failure);
        }

        [Fact]
        public void EfMetadataTypeNestedInAGeneric_IsAViolation()
        {
            var failure = Assert.Single(Evaluate<EfMetadataNestedFixtures.IEfMetadataNestedFixture>());
            Assert.Contains("IEfMetadataNestedFixture.Get", failure);
            Assert.Contains("Microsoft.EntityFrameworkCore.Metadata.IEntityType", failure);
        }

        [Fact]
        public void DtoCarryingAnIQueryableProperty_IsAViolation()
        {
            var failure = Assert.Single(Evaluate<DtoWithQueryablePropertyFixtures.IDtoWithQueryablePropertyFixture>());
            Assert.Contains("IDtoWithQueryablePropertyFixture.Get", failure);
            Assert.Contains("IQueryable", failure);
        }

        [Fact]
        public void ConcreteAggregateReturn_IsGreen()
        {
            Assert.Empty(Evaluate<ConcreteAggregateReturnFixtures.IConcreteAggregateReturnFixture>());
        }

        [Fact]
        public void PagedResultReturn_IsGreen()
        {
            Assert.Empty(Evaluate<PagedResultReturnFixtures.IPagedResultReturnFixture>());
        }

        [Fact]
        public void MoneyReturn_IsGreen()
        {
            Assert.Empty(Evaluate<MoneyReturnFixtures.IMoneyReturnFixture>());
        }

        [Fact]
        public void PrefixMatchingNothing_RedsTheFloorRatherThanPassingOnZero()
        {
            var report = SeamSurfaceScanner.Scan(
                typeof(MoneyReturnFixtures.IMoneyReturnFixture).Assembly,
                ["Cluckwork.Application.Tests.Architecture.SeamFixtures.NoSuchNamespace"],
                minimumInterfaceFloor: 1);

            Assert.Empty(report.InspectedInterfaces);
            var failure = Assert.Single(SeamSurfaceScanner.Evaluate(report));
            Assert.Contains("inspected 0 interface(s)", failure);
        }
    }

    public sealed class SeamSurfaceRealAssemblyTests
    {
        private static Assembly ApplicationAssembly => typeof(IRepository<,>).Assembly;

        [Fact]
        public void RealApplicationAssembly_NoPublicInterfaceExposesPersistence()
        {
            var report = SeamSurfaceScanner.Scan(
                ApplicationAssembly,
                ["Cluckwork.Application.Features", "Cluckwork.Application.Common"],
                minimumInterfaceFloor: 30);

            var failures = SeamSurfaceScanner.Evaluate(report);
            Assert.True(failures.Count == 0, "seam-surface guard failed:\n  " + string.Join("\n  ", failures));
            Assert.True(report.InspectedInterfaces.Count >= 30,
                $"inspected only {report.InspectedInterfaces.Count} interfaces — expected at least 30");
        }

        [Fact]
        public void RealApplicationAssembly_DoesNotReferenceEntityFrameworkOrInfrastructure()
        {
            var failures = SeamSurfaceScanner.EvaluateReferences(ApplicationAssembly);
            Assert.True(failures.Count == 0, "assembly-reference pin failed:\n  " + string.Join("\n  ", failures));
        }
    }
}
