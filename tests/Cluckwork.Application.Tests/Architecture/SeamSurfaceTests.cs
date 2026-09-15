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

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.InheritedGenericBase
{
    using Cluckwork.Domain.Flocks;

    public interface IReader<T>
    {
        T Read();
    }

    public interface IInheritedGenericBaseFixture : IReader<IQueryable<Flock>>
    {
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.NestedPublicInterface
{
    using Cluckwork.Domain.Flocks;

    public static class Contracts
    {
        public interface INestedQueryFixture
        {
            IQueryable<Flock> Get();
        }
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.IndexerParameter
{
    using Cluckwork.Domain.Flocks;

    public interface IIndexerParameterFixture
    {
        int this[IQueryable<Flock> rows] { get; }
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.EventHandlerType
{
    using Cluckwork.Domain.Flocks;

    public interface IEventHandlerTypeFixture
    {
        event Action<IQueryable<Flock>> Changed;
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.QueryableImplementation
{
    using Cluckwork.Domain.Flocks;

    public interface IQueryableImplementationFixture
    {
        EnumerableQuery<Flock> Query();
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.NamedDelegate
{
    using Cluckwork.Domain.Flocks;

    public delegate IQueryable<Flock> QueryFactory();

    public interface INamedDelegateFixture
    {
        QueryFactory Factory();
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.GenericConstraint
{
    using Cluckwork.Domain.Flocks;

    public interface IGenericConstraintFixture
    {
        T Query<T>() where T : IQueryable<Flock>;
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.UnusedGenericConstraint
{
    using Cluckwork.Domain.Flocks;

    public interface IUnusedGenericConstraintFixture
    {
        void Register<T>() where T : IQueryable<Flock>;
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.SelfExpandingGeneric
{
    public sealed class Node<T>
    {
        public T? Value { get; init; }

        public Node<List<T>>? Next { get; init; }
    }

    public interface ISelfExpandingGenericFixture
    {
        Node<int> Root();
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.ExternalBase
{
    using Cluckwork.Domain.Flocks;

    public interface IReader<T>
    {
        IQueryable<T> Read();
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.InheritedExternalMember
{
    using Cluckwork.Domain.Flocks;

    public interface IInheritedExternalMemberFixture : ExternalBase.IReader<Flock>
    {
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.InterfaceConstraint
{
    using Cluckwork.Domain.Flocks;

    public interface IInterfaceConstraintFixture<T> where T : IQueryable<Flock>
    {
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.StaticAbstractOperator
{
    using Cluckwork.Domain.Flocks;

    public interface IStaticAbstractOperatorFixture<TSelf> where TSelf : IStaticAbstractOperatorFixture<TSelf>
    {
        static abstract IQueryable<Flock> operator +(TSelf left, TSelf right);
    }
}

namespace Cluckwork.Application.Tests.Architecture.SeamFixtures.FunctionPointer
{
    using Cluckwork.Domain.Flocks;

    public unsafe interface IFunctionPointerFixture
    {
        delegate*<IQueryable<Flock>, void> Callback();
    }
}

namespace Cluckwork.Application.Tests.Architecture
{
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
    using InheritedGenericBaseFixtures = SeamFixtures.InheritedGenericBase;
    using NestedPublicInterfaceFixtures = SeamFixtures.NestedPublicInterface;
    using IndexerParameterFixtures = SeamFixtures.IndexerParameter;
    using EventHandlerTypeFixtures = SeamFixtures.EventHandlerType;
    using QueryableImplementationFixtures = SeamFixtures.QueryableImplementation;
    using NamedDelegateFixtures = SeamFixtures.NamedDelegate;
    using GenericConstraintFixtures = SeamFixtures.GenericConstraint;
    using SelfExpandingGenericFixtures = SeamFixtures.SelfExpandingGeneric;
    using InheritedExternalMemberFixtures = SeamFixtures.InheritedExternalMember;
    using InterfaceConstraintFixtures = SeamFixtures.InterfaceConstraint;
    using StaticAbstractOperatorFixtures = SeamFixtures.StaticAbstractOperator;
    using FunctionPointerFixtures = SeamFixtures.FunctionPointer;
    using UnusedGenericConstraintFixtures = SeamFixtures.UnusedGenericConstraint;

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
        public void InheritedGenericBaseInterface_SubstitutedArgumentIsAViolation()
        {
            var failures = Evaluate<InheritedGenericBaseFixtures.IInheritedGenericBaseFixture>();

            Assert.Equal(2, failures.Count);
            Assert.Contains(failures, f => f.Contains("IInheritedGenericBaseFixture.: IReader<IQueryable<Flock>>"));
            Assert.Contains(failures, f => f.Contains("IInheritedGenericBaseFixture.Read"));
            Assert.All(failures, f => Assert.Contains("IQueryable", f));
        }

        [Fact]
        public void NestedPublicInterface_IsScanned()
        {
            var failure = Assert.Single(Evaluate<NestedPublicInterfaceFixtures.Contracts.INestedQueryFixture>());
            Assert.Contains("INestedQueryFixture.Get", failure);
        }

        [Fact]
        public void IndexerParameter_IsAViolation()
        {
            var failure = Assert.Single(Evaluate<IndexerParameterFixtures.IIndexerParameterFixture>());
            Assert.Contains("IIndexerParameterFixture.Item", failure);
            Assert.Contains("IQueryable", failure);
        }

        [Fact]
        public void EventHandlerType_IsAViolation()
        {
            var failure = Assert.Single(Evaluate<EventHandlerTypeFixtures.IEventHandlerTypeFixture>());
            Assert.Contains("IEventHandlerTypeFixture.Changed", failure);
            Assert.Contains("IQueryable", failure);
        }

        [Fact]
        public void TypeImplementingIQueryable_IsAViolation()
        {
            var failure = Assert.Single(Evaluate<QueryableImplementationFixtures.IQueryableImplementationFixture>());
            Assert.Contains("IQueryableImplementationFixture.Query", failure);
            Assert.Contains("EnumerableQuery", failure);
        }

        [Fact]
        public void NamedDelegateReturningIQueryable_IsAViolation()
        {
            var failure = Assert.Single(Evaluate<NamedDelegateFixtures.INamedDelegateFixture>());
            Assert.Contains("INamedDelegateFixture.Factory", failure);
            Assert.Contains("IQueryable", failure);
        }

        [Fact]
        public void GenericConstraintOnIQueryable_IsAViolation()
        {
            var failure = Assert.Single(Evaluate<GenericConstraintFixtures.IGenericConstraintFixture>());
            Assert.Contains("IGenericConstraintFixture.Query", failure);
            Assert.Contains("IQueryable", failure);
        }

        [Fact]
        public void SelfExpandingGenericDto_TerminatesAndIsGreen()
        {
            Assert.Empty(Evaluate<SelfExpandingGenericFixtures.ISelfExpandingGenericFixture>());
        }

        [Fact]
        public void MemberInheritedFromAnExternalBaseInterface_IsAViolation()
        {
            var failure = Assert.Single(Evaluate<InheritedExternalMemberFixtures.IInheritedExternalMemberFixture>());
            Assert.Contains("IInheritedExternalMemberFixture.Read", failure);
            Assert.Contains("IQueryable", failure);
        }

        [Fact]
        public void ConstraintOnTheInterfaceOwnTypeParameter_IsAViolation()
        {
            var failure = Assert.Single(Evaluate<InterfaceConstraintFixtures.IInterfaceConstraintFixture<IQueryable<Cluckwork.Domain.Flocks.Flock>>>());
            Assert.Contains("IInterfaceConstraintFixture`1.<T>", failure);
            Assert.Contains("IQueryable", failure);
        }

        [Fact]
        public void StaticAbstractOperator_IsAViolation()
        {
            var report = SeamSurfaceScanner.Scan(
                typeof(StaticAbstractOperatorFixtures.IStaticAbstractOperatorFixture<>).Assembly,
                [typeof(StaticAbstractOperatorFixtures.IStaticAbstractOperatorFixture<>).Namespace!],
                minimumInterfaceFloor: 1);

            var failure = Assert.Single(SeamSurfaceScanner.Evaluate(report));
            Assert.Contains("op_Addition", failure);
            Assert.Contains("IQueryable", failure);
        }

        [Fact]
        public void FunctionPointerParameter_IsAViolation()
        {
            var failure = Assert.Single(Evaluate<FunctionPointerFixtures.IFunctionPointerFixture>());
            Assert.Contains("IFunctionPointerFixture.Callback", failure);
            Assert.Contains("IQueryable", failure);
        }

        [Fact]
        public void ConstraintOnAnOtherwiseUnusedMethodTypeParameter_IsAViolation()
        {
            var failure = Assert.Single(Evaluate<UnusedGenericConstraintFixtures.IUnusedGenericConstraintFixture>());
            Assert.Contains("IUnusedGenericConstraintFixture.Register", failure);
            Assert.Contains("IQueryable", failure);
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
}
