import { newId } from "../../lib/ids";

// An idempotency key and the exact payload it was minted for.
//
// A key identifies ONE attempt at ONE write. Rotating only on success is right
// for retrying the same thing after an ambiguous failure — the server replays
// instead of writing twice — but wrong the moment the payload changes: upload
// logo-v1, lose the response after the server committed it, pick logo-v2
// instead, and the same key replays v1's stored 200. The upload reports success
// and v1 stays (review of #123). Binding the key to the payload keeps the
// retry-dedupe and drops the wrong replay.
export interface Attempt {
  key: string;
  payload: string;
}

export function keyFor(attempt: Attempt | null, payload: string): Attempt {
  return attempt !== null && attempt.payload === payload
    ? attempt
    : { key: newId(), payload };
}
