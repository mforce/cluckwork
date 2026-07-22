// Idempotency keys, and anything else that needs a UUID.
//
// `crypto.randomUUID()` is SECURE-CONTEXT ONLY. On https:// and on localhost it
// exists; on a plain-http origin — which is exactly what a phone gets when it
// opens the dev server at http://192.168.x.x:5173, or any LAN deployment that
// has not been given a certificate — it is undefined, and calling it throws.
//
// Two screens called it during render, so on a phone they threw before painting
// anything and the app showed a black page with no clue why. The rest called it
// from event handlers, so they loaded and then failed on save.
//
// `crypto.getRandomValues()` has no such restriction, so the fallback is a real
// version-4 UUID with the same randomness — not a downgrade to Math.random.
export function newId(): string {
  if (typeof crypto.randomUUID === "function") return crypto.randomUUID();

  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  bytes[6] = (bytes[6] & 0x0f) | 0x40; // version 4
  bytes[8] = (bytes[8] & 0x3f) | 0x80; // variant 10xx (RFC 4122)

  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
  return [
    hex.slice(0, 8), hex.slice(8, 12), hex.slice(12, 16), hex.slice(16, 20), hex.slice(20),
  ].join("-");
}
