import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { encode } from "uqr";
import { QR_OPTIONS } from "./TwoFactorQr";

// #1274 G2.6 — the longest URI the server can build still fits the QR version
// the record measured. #1278's G3.32 writes the fixture from the C# builder; a
// missing file fails here instead of passing on nothing.
const FIXTURE = resolve(process.cwd(), "src/test/otpauth-worst-case.txt");
const PLACEHOLDER = "_".repeat(32);
const DEFAULTS = "&algorithm=SHA1&digits=6&period=30";

describe("G2.6: the worst-case otpauth URI", () => {
  const uri = () => readFileSync(FIXTURE, "utf8").replace(/\n$/, "");

  it("holds the secret placeholder once, 32 characters long", () => {
    expect(uri().match(/_+/g)).toEqual([PLACEHOLDER]);
    expect(new URLSearchParams(uri().slice(uri().indexOf("?") + 1)).get("secret")).toBe(PLACEHOLDER);
  });

  it.each([["as built", ""], ["with the three defaults spelled out", DEFAULTS]])("draws at level M as version 21 or lower, %s", (_, tail) => {
    expect(QR_OPTIONS).toMatchObject({ ecc: "M", boostEcc: false });
    expect(encode(`${uri()}${tail}`, QR_OPTIONS).version).toBeLessThanOrEqual(21);
  });
});
