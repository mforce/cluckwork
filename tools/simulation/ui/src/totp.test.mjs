// #1281 — the harnesses' authenticator codes. The RFC 6238 SHA-1 vector pins
// the code itself; the step rule pins how a process spends the server's window.
// Run: node --test tools/simulation/ui/src/totp.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";
import { STEP_SECONDS, codeSteps, observeWaits, totpCode } from "./totp.ts";

// RFC 6238 appendix B, SHA-1: the ASCII key "12345678901234567890" in base32.
const RFC_KEY = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

test("the code matches the RFC 6238 SHA-1 vectors, last six digits", () => {
  assert.equal(totpCode(RFC_KEY, Math.floor(59 / STEP_SECONDS)), "287082");
  assert.equal(totpCode(RFC_KEY, Math.floor(1111111109 / STEP_SECONDS)), "081804");
  assert.equal(totpCode(RFC_KEY, Math.floor(2000000000 / STEP_SECONDS)), "279037");
});

// The server accepts two steps either side of now, each once. A process walks
// up from the oldest step it has not spent and waits once it would run ahead.
test("a process never offers a step twice and waits past the window", async () => {
  let now = 1_000 * STEP_SECONDS * 1000;
  const waits = [];
  const clock = { now: () => now, sleep: async (ms) => { waits.push(ms); now += ms; } };
  const observed = [];
  observeWaits((ms) => observed.push(ms));
  const offered = [];
  for (let i = 0; i < 7; i++) {
    const steps = codeSteps(clock);
    offered.push((await steps.next()).value);
  }
  assert.deepEqual(offered, [998, 999, 1000, 1001, 1002, 1003, 1004]);
  assert.equal(waits.length, 2, "only the two steps past now + 2 wait");
  assert.deepEqual(observed, waits, "the observer hears each wait before it starts");
});

// reset.sh confirms the Owner's enrolment with the Python helper, and every
// later sign-in uses this one, so the two must agree.
test("reset.sh's Python helper computes the same codes", async () => {
  const { spawnSync } = await import("node:child_process");
  const { fileURLToPath } = await import("node:url");
  const dir = fileURLToPath(new URL("../..", import.meta.url));
  const steps = [1, 37037036, 66666666];
  const run = spawnSync("python3", ["-I", "-c",
    `import sys; sys.path.insert(0, sys.argv[1]); import totp; print(" ".join(totp.code(sys.argv[2], int(s)) for s in sys.argv[3:]))`,
    dir, RFC_KEY, ...steps.map(String)], { encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr);
  assert.equal(run.stdout.trim(), steps.map((step) => totpCode(RFC_KEY, step)).join(" "));
});
