// #1281 — authenticator codes for the simulation Owner, as the server's
// TotpMatcher computes them: RFC 6238, SHA-1, 30-second steps, 6 digits. The
// same rule as tools/simulation/ui/src/totp.ts, with k6/crypto for the HMAC.
//
// The server accepts the steps from two before now to two after, each once.
// Each sign-in in a VU takes the oldest step that VU has not offered, and waits
// for a fresh one rather than run ahead. A step another VU spent is refused as
// reused and uncharged, so login() offers the next one on the same challenge.

import crypto from 'k6/crypto';
import { sleep } from 'k6';

const STEP_SECONDS = 30;
const WINDOW = 2;
const BASE32 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';

let lastStep = 0;

function decodeBase32(key) {
  const bytes = [];
  let buffer = 0;
  let bits = 0;
  for (const char of key.toUpperCase()) {
    buffer = (buffer << 5) | BASE32.indexOf(char);
    bits += 5;
    if (bits >= 8) {
      bits -= 8;
      bytes.push((buffer >> bits) & 0xff);
    }
  }
  return new Uint8Array(bytes).buffer;
}

function code(key, step) {
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
  const step = Math.max(stepNow() - WINDOW, lastStep + 1);
  while (step > stepNow() + WINDOW) sleep(((step - WINDOW) * STEP_SECONDS * 1000 - Date.now()) / 1000);
  lastStep = step;
  return code(key, step);
}
