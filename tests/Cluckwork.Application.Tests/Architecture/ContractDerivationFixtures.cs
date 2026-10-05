// #1087: types in a module's Contracts namespace, read by ContractDerivationTests. No product module has one yet.

using Cluckwork.Domain.Common.Architecture;

namespace Cluckwork.Application.Modules.ContractFixture.Contracts;

public interface IContractFixtureModule;

public sealed record ContractFixtureCommand(Guid Id);

// Shaped like FlockNameResolution: the nested cases stay outside the contract.
public abstract record ContractFixtureResolution
{
    public sealed record Found(Guid Id) : ContractFixtureResolution;

    public sealed record Missing : ContractFixtureResolution;
}

[ModuleContract("ContractFixture")]
public sealed record ContractFixtureMarked;
