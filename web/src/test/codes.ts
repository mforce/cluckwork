// A fresh 6-digit authenticator-code shape per call, so no test holds a code
// literal (#1279).
export function freshCode(): string {
  return crypto.randomUUID().replace(/\D/g, "").padEnd(6, "0").slice(0, 6);
}
