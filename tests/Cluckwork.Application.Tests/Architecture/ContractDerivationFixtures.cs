// #1087: types in a module's Contracts namespace, read by ContractDerivationTests.

namespace Cluckwork.Application.Modules.ContractFixture.Contracts;

public interface IContractFixtureModule;

public sealed record ContractFixtureCommand(Guid Id);

// Shaped like FlockNameResolution: the nested cases stay outside the contract.
public abstract record ContractFixtureResolution
{
    public sealed record Found(Guid Id) : ContractFixtureResolution;

    public sealed record Missing : ContractFixtureResolution;
}
