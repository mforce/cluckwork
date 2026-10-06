// #1087: types in a module's Contracts namespace, read by ContractDerivationTests.

namespace Cluckwork.Application.Modules.ContractFixture.Contracts;

public interface IContractFixtureModule;

public sealed record ContractFixtureCommand(Guid Id);

// Shaped like FlockNameResolution: its public cases are contract too (#1116). A nested type behind a private or
// internal level is not, even when it is itself public.
public abstract record ContractFixtureResolution
{
    private ContractFixtureResolution()
    {
    }

    public sealed record Found(Guid Id) : ContractFixtureResolution;

    public sealed record Missing : ContractFixtureResolution;

    internal sealed record Hidden : ContractFixtureResolution;

    internal static class Internals
    {
        public sealed record Reachable;
    }
}
