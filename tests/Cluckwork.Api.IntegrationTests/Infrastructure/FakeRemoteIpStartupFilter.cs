namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

// The in-process TestServer has no socket peer, so per-IP behavior (rate
// limiting, forwarded-header trust) has nothing to key on. This filter sets
// Connection.RemoteIpAddress from the X-Test-Remote header before the real
// pipeline (and thus before UseForwardedHeaders) runs.
public sealed class FakeRemoteIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (ctx, nextMw) =>
        {
            if (ctx.Request.Headers.TryGetValue("X-Test-Remote", out var remote)
                && IPAddress.TryParse(remote.ToString(), out var ip))
                ctx.Connection.RemoteIpAddress = ip;
            await nextMw();
        });
        next(app);
    };
}
