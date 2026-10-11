#:package ModelContextProtocol@2.2.0
// Inside the repo, opt out of its central package versions and lock files.
#:property ManagePackageVersionsCentrally=false
#:property RestorePackagesWithLockFile=false
// Connects to /mcp with a bearer token at a chosen protocol version and calls one tool.
// Usage: dotnet run probe.cs -- <mcp-url> <token-file> <protocol-version> <tool> [json-args]
using System.Net.Http.Headers;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var (url, tokenFile, version, tool) = (args[0], args[1], args[2], args[3]);
var toolArgs = args.Length > 4 ? JsonDocument.Parse(args[4]).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value) : null;
// The verify stack's TLS proxy uses a throwaway self-signed certificate.
var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(tokenFile).Trim());
var transport = new HttpClientTransport(
    new HttpClientTransportOptions { Endpoint = new Uri(url), TransportMode = HttpTransportMode.StreamableHttp }, http, ownsHttpClient: true);
await using var client = await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = version });
Console.WriteLine($"negotiated={client.NegotiatedProtocolVersion} server={client.ServerInfo.Name}");
Console.WriteLine("tools/list: " + string.Join(", ", (await client.ListToolsAsync()).Select(t => t.Name)));
var result = await client.CallToolAsync(tool, toolArgs);
var text = string.Join("", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
Console.WriteLine($"{tool}: isError={result.IsError == true} {text[..Math.Min(400, text.Length)]}");
