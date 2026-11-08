// #1281 — the harnesses' authenticator codes. The RFC 6238 SHA-1 vectors pin
// the code itself; the step rule pins how a process spends the server's window;
// the retry bound keeps one sign-in short of the account lockout.
// Run: node --test tools/simulation/ui/src/totp.test.mjs
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { MAX_CODE_ATTEMPTS, STEP_SECONDS, codeSteps, totpCode, withCodes } from "./totp.ts";

// RFC 6238 appendix B, SHA-1: its seed is the ASCII "12345678901234567890",
// a public test vector, written here in base32.
function base32(bytes) {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  let bits = "";
  for (const byte of bytes) bits += byte.toString(2).padStart(8, "0");
  return bits.match(/.{1,5}/g).map((chunk) => alphabet[parseInt(chunk.padEnd(5, "0"), 2)]).join("");
}
const RFC_KEY = base32(Buffer.from("12345678901234567890", "ascii"));

const pinned = (seconds) => {
  let now = seconds * 1000;
  const waits = [];
  return { waits, clock: { now: () => now, sleep: async (ms) => { waits.push(ms); now += ms; } } };
};

test("the code matches the RFC 6238 SHA-1 vectors, last six digits", () => {
  assert.equal(totpCode(RFC_KEY, Math.floor(59 / STEP_SECONDS)), "287082");
  assert.equal(totpCode(RFC_KEY, Math.floor(1111111109 / STEP_SECONDS)), "081804");
  assert.equal(totpCode(RFC_KEY, Math.floor(2000000000 / STEP_SECONDS)), "279037");
});

// The server accepts two steps either side of now, each once. A process
// starts one step back and walks up, and waits once it would run ahead.
test("a process never offers a step twice and waits past the window", async () => {
  const { waits, clock } = pinned(1_000 * STEP_SECONDS);
  const offered = [];
  for (let i = 0; i < 7; i++) offered.push((await codeSteps(clock).next()).value);
  assert.deepEqual(offered, [999, 1000, 1001, 1002, 1003, 1004, 1005]);
  assert.equal(waits.length, 3, "only the steps past now + 2 wait");
});

// A code made one second before a boundary and checked one second after it
// must still be inside the server's window (now - 2 to now + 2).
test("a code taken just before a step boundary is still in the window when it arrives", async () => {
  const boundary = 2_000_000 * STEP_SECONDS;
  const { clock } = pinned(boundary - 1);
  const step = (await codeSteps(clock).next()).value;
  const serverStep = Math.floor((boundary + 1) / STEP_SECONDS);
  assert.ok(step >= serverStep - 2, `step ${step} is outside the window at ${serverStep}`);
});

// AccessFailedCount locks the account at 5, so a wrong key must stop first.
test("a wrong key stops before the lockout, naming the key and the clock", async () => {
  const { clock } = pinned(3_000_000 * STEP_SECONDS);
  let submitted = 0;
  await assert.rejects(withCodes(RFC_KEY, async () => { submitted++; return 400; }, clock), /totpKey.*clock/s);
  assert.equal(submitted, MAX_CODE_ATTEMPTS);
  assert.ok(MAX_CODE_ATTEMPTS < 5);
});

test("a refused code is followed by the next step's code, and an accepted one ends the sign-in", async () => {
  const { clock } = pinned(4_000_000 * STEP_SECONDS);
  const codes = [];
  await withCodes(RFC_KEY, async (code) => { codes.push(code); return codes.length === 1 ? 400 : 200; }, clock);
  assert.equal(codes.length, 2);
  assert.notEqual(codes[0], codes[1]);
});

const SIM_DIR = fileURLToPath(new URL("../..", import.meta.url));

// reset.sh confirms the Owner's enrolment with the Python helper, and every
// later sign-in uses this one, so the two must agree.
test("reset.sh's Python helper computes the same codes", () => {
  const steps = [1, 37037036, 66666666];
  const run = spawnSync("python3", ["-I", "-c",
    `import sys; sys.path.insert(0, sys.argv[1]); import totp; print(" ".join(totp.code(sys.argv[2], int(s)) for s in sys.argv[3:]))`,
    SIM_DIR, RFC_KEY, ...steps.map(String)], { encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr);
  assert.equal(run.stdout.trim(), steps.map((step) => totpCode(RFC_KEY, step)).join(" "));
});

// The Python helper's own clock: RFC 6238's T = 59 s is step 1.
test("reset.sh's Python helper reads 30-second steps from the clock", () => {
  const run = spawnSync("python3", ["-I", "-c",
    `import sys, time; sys.path.insert(0, sys.argv[1]); import totp; time.time = lambda: 59; print(totp.code(sys.argv[2]))`,
    SIM_DIR, RFC_KEY], { encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr);
  assert.equal(run.stdout.trim(), "287082");
});
