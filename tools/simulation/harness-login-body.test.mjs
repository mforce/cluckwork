// #1278 G3.21: an enrolled user's failed login carries a sign-in challenge,
// which is a credential. Every harness that signs in is driven through its real
// failure path against a 401 whose challenge is a fresh sentinel, and the
// sentinel must appear in nothing the harness throws or prints.
// Run: node --test tools/simulation/harness-login-body.test.mjs
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { stripTypeScriptTypes } from "node:module";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { inspect } from "node:util";
import { fileURLToPath } from "node:url";
import vm from "node:vm";
import { test } from "node:test";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const read = (path) => readFileSync(join(repo, path), "utf8");
const SELF = "tools/simulation/harness-login-body.test.mjs";

// The pending answer an enrolled user's password step gets. A harness may keep
// its status and title; the challenge, like the rest of a body, is not its to keep.
const problem = (sentinel, title = "Auth.SecondFactorRequired") => ({
  type: "https://tools.ietf.org/html/rfc9110#section-15.5.2",
  title,
  status: 401,
  detail: "Enter the code from your authenticator app.",
  challenge: sentinel,
  challengeExpiresAt: "2099-01-01T00:00:00Z",
});

// Everything a failure can carry: its message, stack and every own property.
const describeError = (error) =>
  error === undefined ? "" : [String(error?.stack ?? error), inspect(error, { depth: 5, showHidden: true })].join("\n");

// A module's top-level code with its imports removed and its exports made
// local, so a vm context can stand in for what it imported.
const asScript = (source) =>
  source.replace(/^import[\s\S]*?from\s+["'][^"']+["'];\n/gm, "").replace(/^export /gm, "");

function capturingConsole(output) {
  const sink = (...values) => output.push(values.map((v) => (typeof v === "string" ? v : inspect(v))).join(" "));
  return { log: sink, error: sink, warn: sink, info: sink, debug: sink };
}

async function driveK6(sentinel) {
  const output = [];
  const body = JSON.stringify(problem(sentinel));
  const context = vm.createContext({
    console: capturingConsole(output),
    http: { post: () => ({ status: 401, headers: {}, body, json: () => JSON.parse(body) }) },
    check: () => false,
    BASE_URL: "http://127.0.0.1:1",
    AUTH_PATHS: { LOGIN: "/api/v1/auth/login", REFRESH: "/api/v1/auth/refresh" },
    CSRF_HEADER_NAME: "X-Cluckwork-Auth",
    CSRF_HEADER_VALUE: "1",
    REFRESH_COOKIE_NAME_PREFIX: "cluckwork_rt_",
  });
  vm.runInContext(asScript(read("tools/simulation/k6/auth.js")), context);
  let error;
  try {
    context.login({ email: "probe@example.invalid", password: randomUUID() });
  } catch (e) {
    error = e;
  }
  return { failed: error !== undefined, text: [...output, describeError(error)].join("\n") };
}

// signInForToken names the pending answer and every other refusal on two
// branches, so it runs once with each title; apiGet shares ApiProbeError.
async function driveProbeApi(sentinel) {
  const output = [];
  let body;
  const response = () => ({
    ok: false, status: 401, statusText: "Unauthorized", headers: new Headers(),
    json: async () => JSON.parse(body), text: async () => body,
  });
  const context = vm.createContext({
    console: capturingConsole(output), fetch: async () => response(), Headers,
    API_PREFIX: "/api/v1", BASE_URL: "http://127.0.0.1:1",
  });
  const source = stripTypeScriptTypes(read("tools/simulation/ui/src/api.ts"));
  vm.runInContext(asScript(source), context);
  const member = { email: "probe@example.invalid", password: randomUUID(), farmCode: "default-farm" };
  const errors = [];
  for (const [title, call] of [
    ["Auth.SecondFactorRequired", () => context.signInForToken(member)],
    ["Identity.InvalidCredentials", () => context.signInForToken(member)],
    ["Identity.InvalidCredentials", () => context.apiGet("token", "/flocks")],
  ]) {
    body = JSON.stringify(problem(sentinel, title));
    try {
      await call();
    } catch (e) {
      errors.push(e);
    }
  }
  return { failed: errors.length === 3, text: [...output, ...errors.map(describeError)].join("\n") };
}

// Runs each reset.sh function that signs in, with python3 wrapped so urlopen
// raises the 401 an enrolled user's password step gets.
async function driveResetScript(sentinel) {
  const script = read("tools/simulation/reset.sh");
  const functions = [...script.matchAll(/^(\w+)\(\) \{\n[\s\S]*?\n\}\n/gm)]
    .filter((match) => /auth\/login|post\("\/login"/.test(match[0]));
  assert.ok(functions.length >= 2, `found ${functions.length} reset.sh functions that sign in, below the floor of 2`);
  const prelude = `
import io, json, os, urllib.error, urllib.request
def _refuse(request, *args, **kwargs):
    body = json.dumps(${JSON.stringify(problem(sentinel))}).encode()
    raise urllib.error.HTTPError(request.full_url, 401, "Unauthorized", {}, io.BytesIO(body))
urllib.request.urlopen = _refuse
`;
  const dir = mkdtempSync(join(tmpdir(), "cw-1278-reset-"));
  try {
    writeFileSync(join(dir, "prelude.py"), prelude);
    // Farm code, email, a password, then 1: rotate takes it as the new password, verify as its flock floor.
    const calls = functions.map((match) => `${match[1]} default-farm probe@example.invalid "$PW" 1 || true`);
    const bash = [
      `python3() { cat "${join(dir, "prelude.py")}" - | command python3 "$@"; }`,
      "APP_PORT=1",
      ...functions.map((match) => match[0]),
      ...calls,
    ].join("\n");
    const run = spawnSync("bash", ["-c", bash], { encoding: "utf8", env: { ...process.env, PW: randomUUID() } });
    // Each function reports its own failure.
    const reports = run.stderr.match(/FAILED/g)?.length ?? 0;
    return { failed: reports >= functions.length, text: run.stdout + run.stderr };
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

// The MCP runbook's login() as written, with curl answering the 401.
async function driveMcpRunbook(sentinel) {
  const line = read("tools/verify/features/mcp.md").split("\n").find((l) => l.startsWith("login() {"));
  assert.ok(line, "the MCP runbook has no login() helper");
  const dir = mkdtempSync(join(tmpdir(), "cw-1278-mcp-"));
  try {
    writeFileSync(join(dir, "answer.json"), JSON.stringify(problem(sentinel)));
    writeFileSync(join(dir, "cast.json"), JSON.stringify({ owner: { email: "probe@example.invalid", password: randomUUID() } }));
    const bash = [
      `curl() { cat "${join(dir, "answer.json")}"; }`,
      `CAST="${join(dir, "cast.json")}"`,
      "APP=http://127.0.0.1:1",
      line,
      `JWT=$(login '{farmCode:"default-farm",email:.owner.email,password:.owner.password}')`,
      `printf 'JWT=%s\\n' "$JWT"`,
    ].join("\n");
    const run = spawnSync("bash", ["-c", bash], { encoding: "utf8" });
    return { failed: /sign-in failed/.test(run.stderr), text: run.stdout + run.stderr };
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

const DRIVERS = {
  "tools/simulation/k6/auth.js": driveK6,
  "tools/simulation/ui/src/api.ts": driveProbeApi,
  "tools/simulation/reset.sh": driveResetScript,
  "tools/verify/features/mcp.md": driveMcpRunbook,
};

// Files that name the login route but cannot be driven outside a live stack,
// each with the reason.
const UNDRIVABLE = {
  "tools/simulation/k6/config.js": "defines the route constant and sends nothing",
  "tools/simulation/ui/src/fixtures.ts": "signs in through the SPA's form in a live browser and reads no response",
  "tools/simulation/ui/src/mutants.ts": "rewrites the SPA's login response inside a live Playwright browser",
  "tools/simulation/ui/specs/session-refresh.spec.ts": "a Playwright spec against the live simulation stack",
  "tools/simulation/ui/specs/session-races.spec.ts": "a Playwright spec against the live simulation stack",
};

test("every harness that signs in has a driver or a stated reason it cannot run", () => {
  const signingIn = execFileSync("git", ["ls-files", "-z", "tools/simulation", "tools/verify"], { cwd: repo, encoding: "utf8" })
    .split("\0").filter(Boolean)
    .filter((path) => path !== SELF && /\/auth\/login|AUTH_PATHS\.LOGIN/.test(read(path)));
  assert.ok(signingIn.length >= 4, `found ${signingIn.length} files that sign in, below the floor of 4`);
  const unknown = signingIn.filter((path) => !(path in DRIVERS) && !(path in UNDRIVABLE));
  assert.deepEqual(unknown, [], "a harness signs in with no driver here; add one, or a reason it cannot run");
  const stale = [...Object.keys(DRIVERS), ...Object.keys(UNDRIVABLE)].filter((path) => !signingIn.includes(path));
  assert.deepEqual(stale, [], "a listed file no longer signs in; remove its entry");
});

for (const [path, drive] of Object.entries(DRIVERS)) {
  test(`${path} reports a failed login without the challenge`, async () => {
    const sentinel = randomUUID();
    const { failed, text } = await drive(sentinel);
    assert.ok(failed, `${path} did not report the failed login at all:\n${text}`);
    assert.ok(!text.includes(sentinel), `${path} printed or kept the sign-in challenge:\n${text}`);
  });
}
