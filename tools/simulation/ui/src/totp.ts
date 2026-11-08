// tools/simulation/ui/src/totp.ts — authenticator codes for the cast's
// twoFactorOwner (#1281), computed as the server's TotpMatcher does: RFC 6238,
// SHA-1, 30-second steps, 6 digits.
//
// The server accepts the steps from two before now to two after, each once
// (#1274 decision 22). A sign-in here starts one step back, not two, so a code
// sent just after a step boundary is still inside the window, and each later
// sign-in in the process takes the next step, waiting rather than running ahead.
//
// A refused code is charged against the account: wrong codes lock it at five.
// withCodes therefore stops after MAX_CODE_ATTEMPTS, so one sign-in never locks
// the Owner out.
//
// Plain functions and no parameter properties, so Node's type stripping runs it
// for totp.test.mjs and for tools/verify/mcp/client.ts.

import { createHmac } from "node:crypto";

export const STEP_SECONDS = 30;
const OLDEST = 1;
const NEWEST = 2;
export const MAX_CODE_ATTEMPTS = 3;
const BASE32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

export interface Clock {
  now(): number;
  sleep(ms: number): Promise<void>;
}

const systemClock: Clock = {
  now: () => Date.now(),
  sleep: (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
};

function decodeBase32(key: string): Buffer {
  const bytes: number[] = [];
  let buffer = 0;
  let bits = 0;
  for (const char of key.toUpperCase()) {
    const value = BASE32.indexOf(char);
    if (value < 0) throw new Error("the authenticator key is not base32");
    buffer = (buffer << 5) | value;
    bits += 5;
    if (bits >= 8) {
      bits -= 8;
      bytes.push((buffer >> bits) & 0xff);
    }
  }
  return Buffer.from(bytes);
}

export function totpCode(key: string, step: number): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(step));
  const hash = createHmac("sha1", decodeBase32(key)).update(counter).digest();
  const offset = hash[hash.length - 1]! & 0x0f;
  return String((hash.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, "0");
}

let lastStep = 0;

const stepAt = (ms: number) => Math.floor(ms / 1000 / STEP_SECONDS);

/** The steps this process may offer, oldest first; it waits rather than run ahead of the window. */
export async function* codeSteps(clock: Clock = systemClock): AsyncGenerator<number, never> {
  for (;;) {
    const step = Math.max(stepAt(clock.now()) - OLDEST, lastStep + 1);
    while (step > stepAt(clock.now()) + NEWEST)
      await clock.sleep((step - NEWEST) * STEP_SECONDS * 1000 - clock.now());
    lastStep = step;
    yield step;
  }
}

/**
 * Submits codes for `key` until one is accepted. `submit` sends one code and
 * answers the response status. A 400 is a refused code, so the next step's
 * code follows on the same challenge; any other failure, or MAX_CODE_ATTEMPTS
 * refusals, throws.
 */
export async function withCodes(key: string, submit: (code: string) => Promise<number>, clock: Clock = systemClock): Promise<void> {
  let attempts = 0;
  for await (const step of codeSteps(clock)) {
    const status = await submit(totpCode(key, step));
    attempts++;
    if (status < 400) return;
    if (status !== 400 || attempts >= MAX_CODE_ATTEMPTS)
      throw new Error(
        `the code step answered ${status} to ${attempts} code(s). Another sign-in may have used this Owner's codes `
          + `in the last minute: wait a minute and try again. If it still fails, check that the cast file's `
          + `twoFactorOwner.totpKey belongs to this stack (re-run \`bash tools/simulation/reset.sh\`) and that this `
          + `host's clock is right.`,
      );
  }
}
