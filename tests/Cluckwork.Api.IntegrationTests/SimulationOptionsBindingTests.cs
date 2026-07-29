namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;

// #243 load-test simulation seeder: Seed:Simulation is the on/off gate on the
// existing SeedOptions, but its shape (counts, timezone, cast password, ...)
// binds separately from its own "Simulation" section — plain in-memory
// IConfiguration binding, no WebApplicationFactory/DB needed. Lives here
// (rather than Application.Tests) because SeedOptions/SimulationOptions are
// Infrastructure types that Application.Tests cannot reference.
public sealed class SimulationOptionsBindingTests
{
    [Fact]
    public void SeedOptions_Simulation_DefaultsToFalse()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var seed = config.GetSection(SeedOptions.SectionName).Get<SeedOptions>() ?? new SeedOptions();

        Assert.False(seed.Simulation);
    }

    [Fact]
    public void SimulationOptions_BindsFromItsOwnSection_SeparateFromSeed()
    {
        // Runtime-generated — never a hardcoded credential literal.
        var castPassword = $"Aa1!{Guid.NewGuid():N}";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seed:Simulation"] = "true",
                ["Simulation:HistoryDays"] = "30",
                ["Simulation:Workers"] = "5",
                ["Simulation:TimeZoneId"] = "Asia/Manila",
                ["Simulation:CastPassword"] = castPassword,
            })
            .Build();

        var seed = config.GetSection(SeedOptions.SectionName).Get<SeedOptions>()!;
        var simulation = config.GetSection(SimulationOptions.SectionName).Get<SimulationOptions>()!;

        Assert.True(seed.Simulation);

        // Overridden values bind correctly.
        Assert.Equal(30, simulation.HistoryDays);
        Assert.Equal(5, simulation.Workers);
        Assert.Equal("Asia/Manila", simulation.TimeZoneId);
        Assert.Equal(castPassword, simulation.CastPassword);

        // Unset fields keep their SimulationOptions defaults.
        Assert.Equal(1, simulation.Managers);
        Assert.Equal(1, simulation.Sales);
        Assert.Equal(4, simulation.ReadOnly);
        Assert.Equal("sim.local", simulation.EmailDomain);
        Assert.Equal(243, simulation.Seed);
        Assert.Null(simulation.CredentialOutputPath);
    }
}
