namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #280 — `dotnet Cluckwork.Api.dll seed --profile demo` is a real CLI dispatch
// branch in Program.cs (args[0] == "seed"), never exercised by
// WebApplicationFactory<Program> — the testing host always passes empty args,
// so that branch is always skipped there. These tests spawn the actual built
// Cluckwork.Api.dll as a *subprocess*, the same binary and entry point an
// operator runs, so the dispatch code genuinely executes end to end (schema
// migrate, profile switch, exit before Kestrel).
//
// Own factory/container (own database), same reasoning as BaselineSeedFactory
// and DemoSeedTests: DemoDataSeeder writes to the fixed SeedDefaults.AccountId,
// so this must not share a database with anything else that seeds it.
public sealed class SeedCommandFixture : CluckworkWebApplicationFactory
{
    // Runtime-generated — never a hardcoded credential.
    public string AdminEmail { get; } = $"seedcmd-{Guid.NewGuid():N}@test.local";
    public string AdminPassword { get; } = $"Aa1!{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Base data only (Account/Admin role/egg grades): scope 1a's CLI
        // dispatch deliberately migrates + runs only the requested profile's
        // seeder — it does not also invoke DatabaseSeeder (base provisioning
        // stays boot-only; #283 is the separate issue for touching that). So
        // this in-process host boots once, the unchanged startupScope base
        // seed runs, and the `seed --profile demo` *subprocess* under test
        // then runs against that already-provisioned database — exactly the
        // "boot the serving process once, then run `seed` against the same
        // database" flow described in Program.cs.
        builder.UseSetting("Seed:Enabled", "true");
        builder.UseSetting("Seed:AdminEmail", AdminEmail);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
    }
}

public sealed class SeedCommandTests : IClassFixture<SeedCommandFixture>
{
    private readonly SeedCommandFixture _factory;
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;

    public SeedCommandTests(SeedCommandFixture factory)
    {
        _factory = factory;
        // Forces host startup (idempotent/cached after the first call): the
        // base seed must have already run before any `seed --profile demo`
        // subprocess below depends on the account it creates.
        _ = _factory.Services;
    }

    private Process StartSeedCommand(string? profile, string environment = "Testing")
    {
        var arguments = profile is null ? "seed" : $"seed --profile {profile}";
        var psi = new ProcessStartInfo("dotnet", $"\"{ApiDllPath}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // A deliberately minimal env — just what Program.cs reads eagerly
        // before the seed dispatch, plus what the demo profile itself needs.
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        psi.Environment["ConnectionStrings__Default"] = _factory.ConnectionString;
        psi.Environment["Database__Provider"] = "Postgres";
        psi.Environment["Jwt__Issuer"] = "cluckwork-test";
        psi.Environment["Jwt__Audience"] = "cluckwork-api-test";
        psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem;
        psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem;
        psi.Environment["Seed__Enabled"] = "true";
        psi.Environment["Seed__Demo"] = "true";
        return Process.Start(psi)!;
    }

    [Fact]
    public async Task SeedCommand_Demo_SeedsDataAndExitsWithoutStartingKestrel()
    {
        using var proc = StartSeedCommand("demo");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        var waitTask = proc.WaitForExitAsync();
        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(60)));
        var exited = completed == waitTask;
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // A hang here would mean the command fell through into app.Run() and
        // bound Kestrel instead of exiting — the whole point of #280.
        Assert.True(exited, $"`seed --profile demo` did not exit within 60s. stdout={stdout} stderr={stderr}");
        Assert.Equal(0, proc.ExitCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var flockCount = await db.Flocks.IgnoreQueryFilters()
            .CountAsync(f => f.AccountId == SeedDefaults.AccountId);
        Assert.Equal(3, flockCount);

        // Re-running is idempotent (DemoDataSeeder's own empty-catalog guard) —
        // exercise it through the same CLI path, not just the seeder directly.
        using var proc2 = StartSeedCommand("demo");
        await proc2.WaitForExitAsync();
        Assert.Equal(0, proc2.ExitCode);
        var flockCountAfterRerun = await db.Flocks.IgnoreQueryFilters()
            .CountAsync(f => f.AccountId == SeedDefaults.AccountId);
        Assert.Equal(3, flockCountAfterRerun);
    }

    [Fact]
    public async Task SeedCommand_UnknownProfile_ExitsNonZeroWithClearMessage()
    {
        using var proc = StartSeedCommand("bogus-profile");
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.Equal(1, proc.ExitCode);
        Assert.Contains("Unknown or missing --profile", stderr);
    }

    [Fact]
    public async Task SeedCommand_MissingProfileFlag_ExitsNonZeroWithClearMessage()
    {
        using var proc = StartSeedCommand(profile: null);
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.Equal(1, proc.ExitCode);
        Assert.Contains("Unknown or missing --profile", stderr);
    }

    // #280 prod guard (defense-in-depth): DemoDataSeeder is only registered
    // outside Production, so resolving it in a Production-env process must
    // fail with a clear operator-facing message — not an opaque DI exception.
    [Fact]
    public async Task SeedCommand_Demo_InProductionEnvironment_FailsCleanly_NotAnOpaqueDiException()
    {
        using var proc = StartSeedCommand("demo", environment: "Production");
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.Equal(1, proc.ExitCode);
        Assert.Contains("not available in Production", stderr);
        // The failure must be the translated message above, not a raw DI
        // resolution exception leaking to the operator's console.
        Assert.DoesNotContain("Unable to resolve service", stderr);
        Assert.DoesNotContain("No service for type", stderr);
    }
}
