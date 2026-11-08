// #1281 — authenticator codes for the cast's twoFactorOwner, as the server's
// TotpMatcher computes them: RFC 6238, SHA-1, 30-second steps, 6 digits. The
// same rule as tools/simulation/ui/src/totp.ts, with k6/crypto for the HMAC;
// totp.test.mjs runs it in Node.
//
// The server accepts the steps from two before now to two after, each once.
// Each sign-in in a VU starts one step back, so a code sent just after a step
// boundary is still inside the window, takes the next step after the one it
// last offered, and waits rather than run ahead. A refused code is charged and
// wrong codes lock the account at five, so withCodes stops at MAX_CODE_ATTEMPTS.

import crypto from 'k6/crypto';
import { sleep } from 'k6';

const STEP_SECONDS = 30;
const OLDEST = 1;
const NEWEST = 2;
export const MAX_CODE_ATTEMPTS = 3;
const BASE32 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';

let lastStep = 0;

function decodeBase32(key) {
  const bytes = [];
  let buffer = 0;
  let bits = 0;
  for (const char of key.toUpperCase()) {
    const value = BASE32.indexOf(char);
    if (value < 0) throw new Error('the authenticator key is not base32');
    buffer = (buffer << 5) | value;
    bits += 5;
    if (bits >= 8) {
      bits -= 8;
      bytes.push((buffer >> bits) & 0xff);
    }
  }
  return new Uint8Array(bytes).buffer;
}

export function code(key, step) {
  const counter = new Uint8Array(8);
  for (let i = 7, value = step; i >= 0; i--, value = Math.floor(value / 256)) counter[i] = value % 256;
  const hex = crypto.hmac('sha1', decodeBase32(key), counter.buffer, 'hex');
  const offset = parseInt(hex.slice(-1), 16);
  const binary = parseInt(hex.slice(offset * 2, offset * 2 + 8), 16) & 0x7fffffff;
  return String(binary % 1000000).padStart(6, '0');
}

const stepNow = () => Math.floor(Date.now() / 1000 / STEP_SECONDS);

/** The next code this VU may offer for `key`; waits rather than run ahead of the window. */
export function nextCode(key) {
  const step = Math.max(stepNow() - OLDEST, lastStep + 1);
  while (step > stepNow() + NEWEST) sleep(((step - NEWEST) * STEP_SECONDS * 1000 - Date.now()) / 1000);
  lastStep = step;
  return code(key, step);
}

/**
 * Calls `submit` with codes for `key` until a response is not a 400 (a refused
 * code) or MAX_CODE_ATTEMPTS were refused, and returns the last response.
 */
export function withCodes(key, submit) {
  let res;
  for (let attempt = 0; attempt < MAX_CODE_ATTEMPTS; attempt++) {
    res = submit(nextCode(key));
    if (res.status !== 400) return res;
  }
  return res;
}
