#:package ModelContextProtocol@2.2.0
// Inside the repo, opt out of its central package versions and lock files.
#:property ManagePackageVersionsCentrally=false
#:property RestorePackagesWithLockFile=false
// Connects to /mcp with a bearer token at a chosen protocol version and calls one tool.
// Usage: dotnet run probe.cs -- <mcp-url> <token-file> <protocol-version> <tool> [json-args]
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var (url, tokenFile, version, tool) = (args[0], args[1], args[2], args[3]);
var toolArgs = args.Length > 4 ? JsonDocument.Parse(args[4]).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value) : null;
// Normal verification, plus the local TLS proxy's throwaway certificate when MCP_TLS_CERT
// names it. Only that exact certificate is trusted, so another host still needs a valid chain.
var pinned = Environment.GetEnvironmentVariable("MCP_TLS_CERT") is { Length: > 0 } pem
    ? X509Certificate2.CreateFromPem(File.ReadAllText(pem)) : null;
var http = new HttpClient(new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
        errors == SslPolicyErrors.None || (pinned is not null && cert is not null && cert.RawData.AsSpan().SequenceEqual(pinned.RawData)),
});
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(tokenFile).Trim());
var transport = new HttpClientTransport(
    new HttpClientTransportOptions { Endpoint = new Uri(url), TransportMode = HttpTransportMode.StreamableHttp }, http, ownsHttpClient: true);
await using var client = await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = version });
Console.WriteLine($"negotiated={client.NegotiatedProtocolVersion} server={client.ServerInfo.Name}");
Console.WriteLine("tools/list: " + string.Join(", ", (await client.ListToolsAsync()).Select(t => t.Name)));
var result = await client.CallToolAsync(tool, toolArgs);
var text = string.Join("", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
Console.WriteLine($"{tool}: isError={result.IsError == true} {text[..Math.Min(400, text.Length)]}");
