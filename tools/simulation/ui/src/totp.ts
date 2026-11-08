// tools/simulation/ui/src/totp.ts — authenticator codes for an enrolled cast
// member (#1281), computed as the server's TotpMatcher does: RFC 6238, SHA-1,
// 30-second steps, 6 digits.
//
// The server accepts the steps from two before now to two after, each once
// (#1274 decision 22). Every sign-in in a process takes the oldest step it has
// not offered yet, so back-to-back sign-ins spend the window and then wait for a
// fresh step. A step another process already spent is refused as reused and
// uncharged, so a caller offers the next one on the same challenge.
//
// Plain functions and no parameter properties, so Node's type stripping runs it
// for totp.test.mjs and for tools/verify/mcp/client.ts.

import { createHmac } from "node:crypto";

export const STEP_SECONDS = 30;
const WINDOW = 2;
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

// Told each wait before it starts. fixtures.ts extends the running test's
// timeout by it, so a fresh step costs the test no part of its own budget.
let onWait: (ms: number) => void = () => {};

export function observeWaits(observer: (ms: number) => void): void {
  onWait = observer;
}

const stepAt = (ms: number) => Math.floor(ms / 1000 / STEP_SECONDS);

/** The steps this process may offer, oldest first; it waits rather than run ahead of the window. */
export async function* codeSteps(clock: Clock = systemClock): AsyncGenerator<number, never> {
  for (;;) {
    const step = Math.max(stepAt(clock.now()) - WINDOW, lastStep + 1);
    while (step > stepAt(clock.now()) + WINDOW) {
      const ms = (step - WINDOW) * STEP_SECONDS * 1000 - clock.now();
      onWait(ms);
      await clock.sleep(ms);
    }
    lastStep = step;
    yield step;
  }
}

/** Codes for `key`, one per step from codeSteps. A caller stops at the first accepted one. */
export async function* codes(key: string, clock: Clock = systemClock): AsyncGenerator<string, void> {
  for await (const step of codeSteps(clock)) yield totpCode(key, step);
}
