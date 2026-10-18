// A real MCP client for /mcp on a running stack: the official TypeScript SDK runs discovery,
// dynamic registration, PKCE and the RFC 8707 resource parameter; Playwright plays the person
// on the consent screen. See README.md for the stack and TLS setup this needs.
//
//   node client.ts connect <persona> <scope> <token-file> [shots-dir]
//   node client.ts tools <token-file>
//   node client.ts call <token-file> <tool> [json-args]
//   node client.ts disconnect <persona> [app-name]
//   node client.ts approve <persona> <authorize-url> [shots-dir]
//
// approve plays the person for another client (Claude Code, Gemini CLI): it opens that
// client's authorize URL and lets the redirect reach the client's own loopback listener.
// MCP_UNTICK and MCP_TICK name scopes (comma-separated) to untick or tick on the consent
// screen before Allow (#1326).
//
// MCP_DETAILS=1 photographs the consent screen with Details open.
// MCP_CLIENT_FILE keeps one registration across connects, so a second connect is the same
// app reconnecting and consent marks what it newly asks for as New (#1311, #1313).
//
// persona: owner | restricted-worker | readme-owner. Credentials come from the
// git-ignored .sim-cast.json and are never printed.
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { createServer } from "node:net";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { UnauthorizedError, type OAuthClientProvider } from "@modelcontextprotocol/sdk/client/auth.js";
import type { OAuthClientInformationMixed, OAuthTokens } from "@modelcontextprotocol/sdk/shared/auth.js";
import { chromium, type Page } from "playwright";
import { en } from "../../../web/src/i18n/en.ts";
import { es } from "../../../web/src/i18n/es.ts";
import { resolveBrowser } from "../../simulation/ui/src/browser.ts";
import { scopeEntry } from "../../../web/src/auth/oauthScopes.ts";

const BASE = process.env.MCP_BASE ?? "https://localhost:8495";
// The throwaway certificate is accepted only for the local proxy. Any other origin keeps
// normal verification, and while the exception is on the browser may reach only BASE.
const LOCAL_PROXY = ["localhost", "127.0.0.1"].includes(new URL(BASE).hostname);
const MCP = new URL("/mcp", BASE);
const APP = "Cluckwork live verifier";
// MCP_LANG=es reads the consent screen in Spanish, for a person whose language is Spanish.
const CONSENT = process.env.MCP_LANG === "es" ? es.connect : en.connect;
const CAST = new URL("../../simulation/.sim-cast.json", import.meta.url);

type Member = { farmCode: string; email: string; password: string };

function persona(name: string): Member {
  const cast = JSON.parse(readFileSync(CAST, "utf8"));
  switch (name) {
    case "owner": return { farmCode: "default-farm", ...cast.owner };
    // The seeder assigns the first Worker one flock (cast.ts restrictedWorker()).
    case "restricted-worker": return { farmCode: "default-farm", ...cast.cast.find((m: { role: string }) => m.role === "Worker") };
    case "readme-owner": return cast.readmeFarm;
    default: throw new Error(`unknown persona ${name}`);
  }
}

async function freePort(): Promise<number> {
  return new Promise((resolve) => {
    const s = createServer().listen(0, "127.0.0.1", () => {
      const port = (s.address() as { port: number }).port;
      s.close(() => resolve(port));
    });
  });
}

async function withPage<T>(run: (page: Page) => Promise<T>): Promise<T> {
  const browser = await chromium.launch({ executablePath: resolveBrowser().executablePath });
  try {
    const context = await browser.newContext({ ignoreHTTPSErrors: LOCAL_PROXY, colorScheme: process.env.MCP_SCHEME === "dark" ? "dark" : "light", viewport: { width: 1280, height: 800 }, deviceScaleFactor: 1 });
    if (LOCAL_PROXY)
      await context.route("**/*", (route) =>
        new URL(route.request().url()).origin === new URL(BASE).origin ? route.fallback() : route.abort());
    const page = await context.newPage();
    const errors: string[] = [];
    page.on("pageerror", (e) => errors.push(e.message));
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    const result = await run(page);
    if (errors.length) console.log(`[console errors] ${errors.join(" | ")}`);
    return result;
  } finally {
    await browser.close();
  }
}

async function signIn(page: Page, member: Member) {
  await page.getByLabel(en.auth.farmCode).fill(member.farmCode);
  await page.getByLabel(en.auth.email).fill(member.email);
  await page.getByLabel(en.auth.password).fill(member.password);
  await page.getByRole("button", { name: en.auth.signIn }).click();
}

const scopeBox = (page: Page, scope: string) => {
  const label = CONSENT[scopeEntry(scope)!.label];
  return page.getByRole("checkbox", { name: new RegExp(`^${label}`) });
};

// The person at the browser: sign in if asked, read the consent screen, type the password,
// Allow, and hand back the code the app's redirect URI receives. With deliver, the redirect
// goes on to the client that is listening for it.
function consent(url: URL, redirect: string, member: Member, shots?: string, deliver = false): Promise<string> {
  return withPage(async (page) => {
    let code: string | null = null;
    let returned = false;
    await page.route(`${redirect}**`, (route) => {
      const back = new URL(route.request().url());
      returned = true;
      code = back.searchParams.get("code");
      console.log(`redirect: ${back.origin}${back.pathname} code=${code ? "present" : "absent"} error=${back.searchParams.get("error")}`);
      return deliver ? route.continue() : route.fulfill({ body: "ok" });
    });
    await page.goto(url.href);
    const farmCode = page.getByLabel(en.auth.farmCode);
    const password = page.getByLabel(CONSENT.password);
    // Cancel is on every consent screen; the password is not when nothing can be ticked.
    const cancel = page.getByRole("button", { name: CONSENT.cancel });
    await farmCode.or(cancel).first().waitFor();
    if (await farmCode.isVisible()) {
      console.log(`sign-in at ${new URL(page.url()).pathname}`);
      await signIn(page, member);
    }
    await cancel.waitFor();
    const main = (await page.locator("main").innerText()).replaceAll(member.email, "<email>");
    console.log(`consent at ${new URL(page.url()).pathname}:\n${main.split("\n").filter(Boolean).map((l) => `  | ${l}`).join("\n")}`);
    const boxes = async () => (await page.getByRole("checkbox").evaluateAll((all) => all.map((b) =>
      `${document.querySelector(`label[for="${b.id}"]`)?.textContent}=${(b as HTMLInputElement).checked ? "ticked" : "unticked"}`))).join(", ");
    console.log(`checkboxes as shown: ${await boxes()}`);
    for (const scope of (process.env.MCP_UNTICK ?? "").split(",").filter(Boolean)) await scopeBox(page, scope).setChecked(false);
    for (const scope of (process.env.MCP_TICK ?? "").split(",").filter(Boolean)) await scopeBox(page, scope).setChecked(true);
    if (process.env.MCP_UNTICK || process.env.MCP_TICK) console.log(`checkboxes at Allow: ${await boxes()}`);
    // MCP_DETAILS=1 opens Details first, so the frames show what the grant covers.
    const details = page.getByText(CONSENT.details, { exact: true });
    if (shots && process.env.MCP_DETAILS && await details.isVisible()) await details.click();
    if (shots) {
      for (const [w, h] of [[1280, 800], [390, 844]] as const)
        for (const theme of ["light", "dark"] as const) {
          await page.setViewportSize({ width: w, height: h });
          await page.emulateMedia({ colorScheme: theme });
          await page.evaluate((t) => { document.documentElement.dataset.theme = t; }, theme);
          await page.screenshot({ path: `${shots}/consent-${w}-${theme}.png`, animations: "disabled", fullPage: !!process.env.MCP_DETAILS });
        }
      await page.setViewportSize({ width: 1280, height: 800 });
    }
    // Nothing ticked leaves only Cancel (#1326).
    if (!(await page.getByRole("button", { name: CONSENT.allow }).isVisible())) {
      await page.getByRole("button", { name: CONSENT.cancel }).click();
      for (let i = 0; i < 100 && !returned; i++) await page.waitForTimeout(100);
      return "";
    }
    // The password field is required, so this checks form validation only; the server's
    // step-up refusal is checked with a direct request (features/mcp.md).
    await page.getByRole("button", { name: CONSENT.allow }).click();
    await page.waitForTimeout(500);
    console.log(`form validation: Allow with no password stays on consent: ${!code && (await password.isVisible())}`);
    await password.fill(member.password);
    await page.getByRole("button", { name: CONSENT.allow }).click();
    for (let i = 0; i < 100 && !code; i++) await page.waitForTimeout(100);
    if (!code) throw new Error("no authorization code reached the redirect URI");
    return code;
  });
}

// The person removes the app on Account › Connected apps, as the runbook's section C says.
function disconnect(member: Member, app: string): Promise<void> {
  return withPage(async (page) => {
    await page.goto(new URL("/login", BASE).href);
    await signIn(page, member);
    await page.locator("main#main-content").waitFor();
    await page.goto(new URL("/account", BASE).href);
    // Each app is a collapsed <details>; its Disconnect button shows once the row is open.
    await page.locator("summary", { hasText: app }).first().click();
    await page.getByRole("button", { name: en.connectedApps.disconnectApp.replace("{{app}}", app) }).first().click();
    await page.getByRole("dialog").getByRole("button", { name: en.connectedApps.disconnect }).click();
    const done = en.connectedApps.disconnected.replace("{{app}}", app);
    await page.getByText(done).first().waitFor();
    console.log(`account page: "${done}"`);
  });
}

async function connect(name: string, scope: string, tokenFile: string, shots?: string) {
  const member = persona(name);
  const clientFile = process.env.MCP_CLIENT_FILE;
  const saved = clientFile && existsSync(clientFile)
    ? JSON.parse(readFileSync(clientFile, "utf8")) as { info: OAuthClientInformationMixed; redirect: string }
    : undefined;
  const redirect = saved?.redirect ?? `http://127.0.0.1:${await freePort()}/callback`;
  let info: OAuthClientInformationMixed | undefined = saved?.info;
  if (saved) console.log(`reusing client_id=${saved.info.client_id}`);
  let tokens: OAuthTokens | undefined;
  let verifier = "";
  let authUrl: URL | undefined;
  const provider: OAuthClientProvider = {
    redirectUrl: redirect,
    clientMetadata: {
      client_name: APP,
      redirect_uris: [redirect],
      grant_types: ["authorization_code"],
      response_types: ["code"],
      token_endpoint_auth_method: "none",
      scope,
    },
    clientInformation: () => info,
    saveClientInformation: (i) => {
      info = i;
      console.log(`registered client_id=${i.client_id}`);
      if (clientFile) writeFileSync(clientFile, JSON.stringify({ info: i, redirect }), { mode: 0o600 });
    },
    tokens: () => tokens,
    saveTokens: (t) => { tokens = t; },
    // The SDK asks for every scope the resource metadata lists and ignores clientMetadata.scope,
    // so the client narrows its own request here, as any client may.
    redirectToAuthorization: (u) => { u.searchParams.set("scope", scope); authUrl = u; },
    saveCodeVerifier: (v) => { verifier = v; },
    codeVerifier: () => verifier,
  };

  const first = new StreamableHTTPClientTransport(MCP, { authProvider: provider });
  try {
    await new Client({ name: "cluckwork-live-verifier", version: "1" }).connect(first);
    throw new Error("connected without authorization");
  } catch (e) {
    if (!(e instanceof UnauthorizedError)) throw e;
  }
  const q = authUrl!.searchParams;
  console.log(`authorize: ${authUrl!.origin}${authUrl!.pathname} scope=${q.get("scope")} resource=${q.get("resource")} code_challenge_method=${q.get("code_challenge_method")}`);
  const code = await consent(authUrl!, redirect, member, shots);
  if (!code) {
    console.log("cancelled: no code, nothing connected");
    return;
  }
  await first.finishAuth(code);
  console.log(`token: type=${tokens!.token_type} expires_in=${tokens!.expires_in ?? "none"} scope=${tokens!.scope ?? "(not echoed)"} refresh=${tokens!.refresh_token ? "yes" : "no"}`);
  writeFileSync(tokenFile, tokens!.access_token, { mode: 0o600 });

  const client = new Client({ name: "cluckwork-live-verifier", version: "1" });
  const transport = new StreamableHTTPClientTransport(MCP, { authProvider: provider });
  await client.connect(transport);
  console.log(`protocol=${transport.protocolVersion ?? "?"} server=${JSON.stringify(client.getServerVersion())}`);
  console.log(`tools/list: ${(await client.listTools()).tools.map((t) => t.name).join(", ") || "(none)"}`);
  await client.close();
}

async function bearerClient(tokenFile: string) {
  const token = readFileSync(tokenFile, "utf8").trim();
  const client = new Client({ name: "cluckwork-live-verifier", version: "1" });
  await client.connect(new StreamableHTTPClientTransport(MCP, { requestInit: { headers: { Authorization: `Bearer ${token}` } } }));
  return client;
}

const [cmd, ...rest] = process.argv.slice(2);
try {
  if (cmd === "connect") await connect(rest[0]!, rest[1]!, rest[2]!, rest[3]);
  else if (cmd === "disconnect") await disconnect(persona(rest[0]!), rest[1] ?? APP);
  else if (cmd === "approve") {
    const url = new URL(rest[1]!);
    await consent(url, url.searchParams.get("redirect_uri")!, persona(rest[0]!), rest[2], true);
  }
  else if (cmd === "tools") {
    const c = await bearerClient(rest[0]!);
    console.log((await c.listTools()).tools.map((t) => t.name).join(", ") || "(none)");
    await c.close();
  } else if (cmd === "call") {
    const c = await bearerClient(rest[0]!);
    const r = await c.callTool({ name: rest[1]!, arguments: rest[2] ? JSON.parse(rest[2]) : {} });
    // Text only, so a successful reply pipes straight into jq.
    if (r.isError) console.log("isError: true");
    for (const c of r.content as { type: string; text?: string }[]) if (c.type === "text") console.log(c.text);
    await c.close();
  } else throw new Error("usage: connect <persona> <scope> <token-file> [shots-dir] | approve <persona> <authorize-url> [shots-dir] | tools <token-file> | call <token-file> <tool> [json-args] | disconnect <persona> [app-name]");
} catch (e) {
  const err = e as { code?: number; message?: string };
  console.log(`ERROR code=${err.code ?? "-"} ${err.message}`);
  process.exitCode = 1;
}
