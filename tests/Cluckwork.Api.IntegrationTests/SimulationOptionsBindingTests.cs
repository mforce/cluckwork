namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;

// #243 load-test simulation seeder. #279: there is deliberately NO Seed:Simulation
// gate anymore — the seeder is invoked only by the explicit `seed --profile
// simulation` command (that dispatch + its Production guard is the gate). Its
// shape (counts, timezone, cast password, ...) still binds from its own
// "Simulation" section — plain in-memory IConfiguration binding, no
// WebApplicationFactory/DB needed. Lives here (rather than Application.Tests)
// because SimulationOptions is an Infrastructure type that Application.Tests
// cannot reference.
public sealed class SimulationOptionsBindingTests
{
    [Fact]
    public void SimulationOptions_BindsFromItsOwnSection()
    {
        // Runtime-generated — never a hardcoded credential literal.
        var castPassword = $"Aa1!{Guid.NewGuid():N}";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Simulation:HistoryDays"] = "30",
                ["Simulation:Workers"] = "5",
                ["Simulation:TimeZoneId"] = "Asia/Manila",
                ["Simulation:CastPassword"] = castPassword,
            })
            .Build();

        var simulation = config.GetSection(SimulationOptions.SectionName).Get<SimulationOptions>()!;

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
