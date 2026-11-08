// #1281 — k6's authenticator codes, run in Node with stand-ins for k6/crypto,
// k6's sleep and a pinned clock. The same RFC 6238 vectors and step rule as
// tools/simulation/ui/src/totp.test.mjs.
// Run: node --test tools/simulation/k6/totp.test.mjs
import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import vm from "node:vm";

const SOURCE = readFileSync(new URL("./totp.js", import.meta.url), "utf8")
  .replace(/^import[\s\S]*?from\s+'[^']+';\n/gm, "")
  .replace(/^export /gm, "");

// RFC 6238 appendix B, SHA-1: its public seed, the ASCII "12345678901234567890", in base32.
function base32(bytes) {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  let bits = "";
  for (const byte of bytes) bits += byte.toString(2).padStart(8, "0");
  return bits.match(/.{1,5}/g).map((chunk) => alphabet[parseInt(chunk.padEnd(5, "0"), 2)]).join("");
}
const RFC_KEY = base32(Buffer.from("12345678901234567890", "ascii"));

// A fresh copy of totp.js per test, on a clock pinned at `seconds`.
function load(seconds) {
  let now = seconds * 1000;
  const sleeps = [];
  const context = vm.createContext({
    Uint8Array, parseInt, String, Math, Error,
    Date: { now: () => now },
    sleep: (secs) => { sleeps.push(secs); now += secs * 1000; },
    crypto: {
      hmac: (algorithm, secret, data, encoding) =>
        createHmac(algorithm, Buffer.from(secret)).update(Buffer.from(data)).digest(encoding),
    },
  });
  vm.runInContext(`${SOURCE}\nglobalThis.MAX = MAX_CODE_ATTEMPTS;\nglobalThis.HINT = CODE_STEP_HINT;`, context);
  return { context, sleeps };
}

test("the code matches the RFC 6238 SHA-1 vectors, last six digits", () => {
  const { context } = load(0);
  assert.equal(context.code(RFC_KEY, 1), "287082");
  assert.equal(context.code(RFC_KEY, 37037036), "081804");
  assert.equal(context.code(RFC_KEY, 66666666), "279037");
});

test("a key that is not base32 is refused", () => {
  const { context } = load(0);
  assert.throws(() => context.code("not base32!", 1), /base32/);
});

test("a VU never offers a step twice, starts one step back, and waits past the window", () => {
  const { context, sleeps } = load(1_000 * 30);
  const offered = Array.from({ length: 7 }, () => context.nextCode(RFC_KEY));
  assert.deepEqual(offered, [999, 1000, 1001, 1002, 1003, 1004, 1005].map((step) => context.code(RFC_KEY, step)));
  assert.equal(sleeps.length, 3, "only the steps past now + 2 wait");
});

test("a wrong key stops before the lockout", () => {
  const { context } = load(2_000 * 30);
  let submitted = 0;
  const answer = context.withCodes(RFC_KEY, () => { submitted++; return { status: 400 }; });
  assert.equal(answer.status, 400);
  assert.equal(submitted, context.MAX);
  assert.ok(context.MAX < 5);
});

// login() adds this hint when the code step fails; wrong codes count across runs.
test("the failure hint names every cause and the lockout", () => {
  const { context } = load(0);
  for (const cause of [/another sign-in/i, /totpKey/, /clock/, /locked/, /second failing run.*15 minutes/s])
    assert.match(context.HINT, cause);
});

test("an accepted code ends the sign-in", () => {
  const { context } = load(3_000 * 30);
  let submitted = 0;
  const answer = context.withCodes(RFC_KEY, () => ({ status: ++submitted === 2 ? 200 : 400 }));
  assert.equal(answer.status, 200);
  assert.equal(submitted, 2);
});

// Only a 400 is a refused code; an expired challenge or a limit is final.
test("any failure but a refused code stops at once", () => {
  const { context } = load(4_000 * 30);
  let submitted = 0;
  const answer = context.withCodes(RFC_KEY, () => { submitted++; return { status: 401 }; });
  assert.equal(answer.status, 401);
  assert.equal(submitted, 1);
});
