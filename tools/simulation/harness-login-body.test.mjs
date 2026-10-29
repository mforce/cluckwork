// #1278 G3.21: an enrolled user's failed login carries a sign-in challenge,
// which is a credential. Every harness that signs in is driven through its real
// failure path against a 401 whose challenge is a fresh sentinel. The harness
// must report that failure in its own words, the sentinel must appear in
// nothing it prints or throws, and a thrown error may hold only its primitive
// fields. The sentinel is matched whole: a harness has no reason to encode a
// body before printing it.
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

// One rule for "signs in", for discovery and for picking reset.sh's functions.
const signsIn = (text) => /\/auth\/login|AUTH_PATHS\.LOGIN|post\("\/login"/.test(text);

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

// A thrown error may hold only these fields, each a primitive data property,
// and its native stack. Any other own property, an accessor or a member its
// class adds is a problem, so an error cannot keep a body, a response, a Map
// or a getter over either.
const ERROR_FIELDS = new Set(["name", "message", "status", "title"]);

function errorShape(error) {
  if (typeof error !== "object" || error === null) return { problems: [], strings: [String(error)] };
  const problems = [];
  const strings = [];
  let proto = Object.getPrototypeOf(error);
  for (; proto && proto.constructor?.name !== "Error"; proto = Object.getPrototypeOf(proto))
    for (const key of Reflect.ownKeys(proto)) if (key !== "constructor") problems.push(`the thrown error's class adds ${String(key)}`);
  // The stack accessor of the error's own realm: the vm's, or the test's for a stand-in.
  const nativeStack = proto ? Object.getOwnPropertyDescriptor(new proto.constructor(), "stack") : {};
  for (const key of Reflect.ownKeys(error)) {
    const descriptor = Object.getOwnPropertyDescriptor(error, key);
    if (key === "stack" && descriptor.get === nativeStack.get && descriptor.set === nativeStack.set) strings.push(String(error.stack));
    else if (ERROR_FIELDS.has(key) && "value" in descriptor && Object(descriptor.value) !== descriptor.value) strings.push(String(descriptor.value));
    else problems.push(`the thrown error holds ${String(key)}, which is not one of its primitive fields`);
  }
  return { problems, strings };
}

// A module's top-level code with its imports removed and its exports made
// local, so a vm context can stand in for what it imported. Any import the
// sandbox does not stand in for fails, rather than leaving a name undefined.
function asScript(source, allowedImports) {
  const imports = [...source.matchAll(/^import[\s\S]*?from\s+["']([^"']+)["'];\n/gm)].map((m) => m[1]);
  assert.deepEqual(imports.filter((from) => !allowedImports.includes(from)), [], "the sandbox does not stand in for these imports");
  return source.replace(/^import[\s\S]*?from\s+["'][^"']+["'];\n/gm, "").replace(/^export /gm, "");
}

function capturingConsole(output) {
  const sink = (...values) => output.push(values.map((v) => (typeof v === "string" ? v : inspect(v, { depth: null }))).join(" "));
  return { log: sink, error: sink, warn: sink, info: sink, debug: sink };
}

async function driveK6(sentinel) {
  const output = [];
  const body = JSON.stringify(problem(sentinel));
  let posted = 0;
  const context = vm.createContext({
    console: capturingConsole(output),
    http: {
      post: () => {
        posted++;
        return { status: 401, headers: {}, body, json: () => JSON.parse(body) };
      },
    },
    // k6's check prints each check's name and runs its predicate; fail throws.
    check: (response, checks) => {
      output.push(...Object.keys(checks));
      return Object.values(checks).map((predicate) => predicate(response)).every(Boolean);
    },
    fail: (message) => {
      throw new Error(message);
    },
    BASE_URL: "http://127.0.0.1:1",
    AUTH_PATHS: { LOGIN: "/api/v1/auth/login", REFRESH: "/api/v1/auth/refresh" },
    CSRF_HEADER_NAME: "X-Cluckwork-Auth",
    CSRF_HEADER_VALUE: "1",
    REFRESH_COOKIE_NAME_PREFIX: "cluckwork_rt_",
  });
  vm.runInContext(asScript(read("tools/simulation/k6/auth.js"), ["k6/http", "k6", "./config.js"]), context);
  let error;
  try {
    context.login({ email: "probe@example.invalid", password: randomUUID() });
  } catch (e) {
    error = e;
  }
  const unmet = [];
  if (posted !== 1) unmet.push(`login() posted ${posted} times, not once`);
  // Its own error: a plain Error naming the status, not a TypeError or a
  // ReferenceError from a sandbox that no longer fits the file.
  if (error?.constructor?.name !== "Error" || !/status=401/.test(String(error.message)))
    unmet.push(`login() did not throw its own failure naming status=401: ${String(error)}`);
  const shape = errorShape(error);
  return { unmet: [...unmet, ...shape.problems], text: [...output, ...shape.strings].join("\n") };
}

// signInForToken names the pending answer and every other refusal on two
// branches, so it runs once with each title; apiGet shares ApiProbeError.
async function driveProbeApi(sentinel) {
  const output = [];
  let title;
  let fetched = 0;
  const response = () =>
    new Response(JSON.stringify(problem(sentinel, title)), { status: 401, headers: { "content-type": "application/problem+json" } });
  const context = vm.createContext({
    console: capturingConsole(output),
    fetch: async () => {
      fetched++;
      return response();
    },
    Response, Headers,
    API_PREFIX: "/api/v1", BASE_URL: "http://127.0.0.1:1",
  });
  const source = stripTypeScriptTypes(read("tools/simulation/ui/src/api.ts"));
  vm.runInContext(asScript(source, ["./env"]), context);
  const member = { email: "probe@example.invalid", password: randomUUID(), farmCode: "default-farm" };
  const unmet = [];
  const strings = [];
  for (const [answer, name, call] of [
    ["Auth.SecondFactorRequired", "signInForToken", () => context.signInForToken(member)],
    ["Identity.InvalidCredentials", "signInForToken", () => context.signInForToken(member)],
    ["Identity.InvalidCredentials", "apiGet", () => context.apiGet("token", "/flocks")],
  ]) {
    title = answer;
    let error;
    try {
      await call();
    } catch (e) {
      error = e;
    }
    if (error?.name !== "ApiProbeError" || error.status !== 401 || error.title !== answer)
      unmet.push(`${name} with ${answer} did not throw ApiProbeError(401, ${answer}): ${String(error)}`);
    const shape = errorShape(error);
    unmet.push(...shape.problems.map((issue) => `${name} with ${answer}: ${issue}`));
    strings.push(...shape.strings);
  }
  if (fetched !== 3) unmet.push(`fetch was called ${fetched} times, not 3`);
  return { unmet, text: [...output, ...strings].join("\n") };
}


// Runs each reset.sh function that signs in, on its own, with python3 wrapped
// so urlopen raises the 401 an enrolled user's password step gets.
async function driveResetScript(sentinel) {
  const functions = [...read("tools/simulation/reset.sh").matchAll(/^(\w+)\(\) \{\n[\s\S]*?\n\}\n/gm)]
    .filter((match) => signsIn(match[0]));
  assert.ok(functions.length >= 2, `found ${functions.length} reset.sh functions that sign in, below the floor of 2`);
  const prelude = `
import io, json, os, urllib.error, urllib.request
def _refuse(request, *args, **kwargs):
    with open(os.environ["CW_CALLS"], "a") as calls:
        calls.write(request.full_url + "\\n")
    body = json.dumps(${JSON.stringify(problem(sentinel))}).encode()
    raise urllib.error.HTTPError(request.full_url, 401, "Unauthorized", {}, io.BytesIO(body))
urllib.request.urlopen = _refuse
`;
  const dir = mkdtempSync(join(tmpdir(), "cw-1278-reset-"));
  try {
    const calls = join(dir, "calls.txt");
    writeFileSync(join(dir, "prelude.py"), prelude);
    const unmet = [];
    const texts = [];
    for (const [definition, name] of functions) {
      writeFileSync(calls, "");
      // Farm code, email, a password, then 1: rotate takes it as the new password, verify as its flock floor.
      const bash = [
        `python3() { cat "${join(dir, "prelude.py")}" - | command python3 "$@"; }`,
        "APP_PORT=1",
        definition,
        `${name} default-farm probe@example.invalid "$PW" 1`,
      ].join("\n");
      const run = spawnSync("bash", ["-c", bash], { encoding: "utf8", env: { ...process.env, PW: randomUUID(), CW_CALLS: calls } });
      if (!readFileSync(calls, "utf8").includes("/login")) unmet.push(`${name} never reached the login request`);
      if (run.status === 0 || !/FAILED: .*401/.test(run.stderr)) unmet.push(`${name} did not report its own 401 failure`);
      texts.push(run.stdout, run.stderr);
    }
    return { unmet, text: texts.join("\n") };
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
      `curl() { cat "${join(dir, "answer.json")}"; echo curled >> "${join(dir, "calls.txt")}"; }`,
      `CAST="${join(dir, "cast.json")}"`,
      "APP=http://127.0.0.1:1",
      line,
      `JWT=$(login '{farmCode:"default-farm",email:.owner.email,password:.owner.password}')`,
      `printf 'JWT=%s\\n' "$JWT"`,
    ].join("\n");
    const run = spawnSync("bash", ["-c", bash], { encoding: "utf8" });
    const unmet = [];
    writeFileSync(join(dir, "calls.txt"), "", { flag: "a" });
    if (!readFileSync(join(dir, "calls.txt"), "utf8").includes("curled")) unmet.push("login() never called curl");
    if (!/sign-in failed: 401 Auth\.SecondFactorRequired/.test(run.stderr)) unmet.push("login() did not report its own 401 failure");
    return { unmet, text: run.stdout + run.stderr };
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
// each with what it does with a login response.
const UNDRIVABLE = {
  "tools/simulation/k6/config.js": "defines the route constant and sends nothing",
  "tools/simulation/ui/src/fixtures.ts": "signs in through the SPA's form in a live browser; it never reads a login response",
  "tools/simulation/ui/src/mutants.ts": "rewrites a successful login response's role inside a live Playwright browser; it prints no body",
  "tools/simulation/ui/specs/session-refresh.spec.ts": "reads only a successful (r.ok()) login response's token, against the live stack",
  "tools/simulation/ui/specs/session-races.spec.ts": "records that a login request was sent, against the live stack; it reads no body",
};

test("every harness that signs in has a driver or a stated reason it cannot run", () => {
  const signingIn = execFileSync("git", ["ls-files", "-z", "tools/simulation", "tools/verify"], { cwd: repo, encoding: "utf8" })
    .split("\0").filter(Boolean)
    .filter((path) => path !== SELF && signsIn(read(path)));
  assert.ok(signingIn.length >= 4, `found ${signingIn.length} files that sign in, below the floor of 4`);
  const unknown = signingIn.filter((path) => !(path in DRIVERS) && !(path in UNDRIVABLE));
  assert.deepEqual(unknown, [], "a harness signs in with no driver here; add one, or a reason it cannot run");
  const stale = [...Object.keys(DRIVERS), ...Object.keys(UNDRIVABLE)].filter((path) => !signingIn.includes(path));
  assert.deepEqual(stale, [], "a listed file no longer signs in; remove its entry");
});

for (const [path, drive] of Object.entries(DRIVERS)) {
  test(`${path} reports a failed login without the challenge`, async () => {
    const sentinel = randomUUID();
    const { unmet, text } = await drive(sentinel);
    assert.deepEqual(unmet, [], `${path} did not report the failed login itself:\n${text}`);
    assert.ok(!text.includes(sentinel), `${path} printed or kept the sign-in challenge:\n${text}`);
  });
}
