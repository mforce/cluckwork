# MCP live clients

Real MCP clients for verifying `/mcp` on a running stack. The procedure, the checks and
what each proves are in [`../features/mcp.md`](../features/mcp.md).

| File | What it is |
| --- | --- |
| `tls-proxy.mjs` | Terminates TLS for the http sim stack, keeps `Host`, sets `X-Forwarded-Proto`. |
| `client.ts` | The official TypeScript MCP SDK client. `connect` runs discovery, dynamic registration, PKCE and `resource`, and drives the consent screen with Playwright; `tools`, `call` and `disconnect` use a saved token. Speaks MCP 2025-11-25. |
| `probe.cs` | The C# `ModelContextProtocol` 2.2.0 client (the server's SDK), as a file-based app, for MCP 2026-07-28. |

```sh
npm ci                                   # Node 22.18+ runs client.ts directly
node client.ts connect owner farm:read ~/.tmp/mcp-verify/owner-read.token ~/.tmp/mcp-verify/shots
dotnet run probe.cs -- https://localhost:8495/mcp ~/.tmp/mcp-verify/owner-read.token 2026-07-28 cluckwork_read_stock
```

`MCP_BASE` overrides `https://localhost:8495`; `MCP_SCHEME=dark` starts the browser dark.
`NODE_EXTRA_CA_CERTS` and `MCP_TLS_CERT` name the proxy's generated certificate for Node and
`probe.cs`; certificate errors are ignored nowhere else.
Personas are `owner`, `restricted-worker` and `readme-owner`. Keep tokens outside the repo.
