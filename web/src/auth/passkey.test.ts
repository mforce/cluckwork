import { describe, it, expect } from "vitest";
import { webAuthnHost } from "./passkey";

// #1285 — WebAuthn refuses an IP address as the relying party.
describe("webAuthnHost", () => {
  it.each(["127.0.0.1", "10.0.0.1", "[::1]", "[fe80::1]"])("refuses the IP address %s", (host) => {
    expect(webAuthnHost(host)).toBe(false);
  });

  it.each(["localhost", "farm.example.test", "1farm.example", "10.0.0.1.nip.io"])("accepts the host name %s", (host) => {
    expect(webAuthnHost(host)).toBe(true);
  });
});
