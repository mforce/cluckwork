import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { productionStepUpUses, stepUpUses } from "./stepUpCallers";

// #1279 — the walk's own behaviour, pinned against synthetic sources, so a
// caller written in a shape the real tree lacks today is still found, and any
// other use of the binding is reported.
const FILE = resolve(process.cwd(), "src/routes/users/useFake.ts");
const uses = (source: string) => stepUpUses(source, FILE);
const sites = (source: string) => uses(source).sites;
const flagged = (source: string) => uses(source).violations.length;

const DIRECT = 'import { stepUp } from "../../api/client";\n';
const NAMESPACE = 'import * as api from "../../api/client";\n';

describe("stepUpUses: calls", () => {
  it.each([
    ["an aliased import", 'import { stepUp as prove } from "../../api/client";\nexport function useFake() { async function onSave() { await prove({ password: "" }); } }'],
    ["odd spacing", `${DIRECT}export function useFake() { async function onSave() { await stepUp  (\n{ password: "" }); } }`],
    ["an optional call", `${DIRECT}export function useFake() { async function onSave() { await stepUp?.({ password: "" }); } }`],
    ["a dotted namespace call", `${NAMESPACE}export function useFake() { async function onSave() { await api.stepUp({ password: "" }); } }`],
    ["a computed namespace call", `${NAMESPACE}export function useFake() { async function onSave() { await api["stepUp"]({ password: "" }); } }`],
    ["an optional namespace member", `${NAMESPACE}export function useFake() { async function onSave() { await api?.stepUp({ password: "" }); } }`],
    ["an optional namespace call", `${NAMESPACE}export function useFake() { async function onSave() { await api.stepUp?.({ password: "" }); } }`],
    ["an import spelled with .ts", 'import { stepUp } from "../../api/session.ts";\nexport function useFake() { async function onSave() { await stepUp({ password: "" }); } }'],
    ["an import spelled with .js", 'import { stepUp } from "../../api/client.js";\nexport function useFake() { async function onSave() { await stepUp({ password: "" }); } }'],
    ["a function-valued const", `${DIRECT}export const useFake = () => { const onSave = async () => { await stepUp({ password: "" }); }; };`],
  ])("finds the call through %s", (_, source) => {
    expect(uses(source)).toEqual({ sites: ["useFake.onSave"], violations: [] });
  });

  it("reports two calls in one function twice", () => {
    expect(sites(`${DIRECT}export function useFake() { async function onSave() { await stepUp({ password: "" }); await stepUp({ password: "" }); } }`))
      .toEqual(["useFake.onSave", "useFake.onSave"]);
  });

  it("ignores a stepUp that is not the API's, and other members of the namespace", () => {
    expect(uses(`import { stepUp } from "./elsewhere";\n${NAMESPACE}export function useFake() { stepUp(); api.logout(); api.NO_PROOF; }`))
      .toEqual({ sites: [], violations: [] });
  });
});

describe("stepUpUses: every other use is a violation", () => {
  it.each([
    ["a local re-export", `${DIRECT}export { stepUp };`],
    ["an aliased local re-export", `${DIRECT}export { stepUp as prove };`],
    ["a default export", `${DIRECT}export default stepUp;`],
    ["a const alias", `${DIRECT}export const prove = stepUp;`],
    ["a value in an object", `${DIRECT}export const api = { stepUp };`],
    ["an argument", `${DIRECT}run(stepUp);`],
    ["stepUp.call", `${DIRECT}stepUp.call(null, { password: "" });`],
    ["a non-null call", `${DIRECT}stepUp!({ password: "" });`],
    ["a sequence call", `${DIRECT}(0, stepUp)({ password: "" });`],
    ["a namespace destructured", `${NAMESPACE}const { stepUp } = api;`],
    ["a namespace passed on", `${NAMESPACE}run(api);`],
    ["a computed namespace member", `${NAMESPACE}const name = "stepUp";\napi[name]({ password: "" });`],
    ["a namespace member read, not called", `${NAMESPACE}export const prove = api.stepUp;`],
    ["a dynamic import", 'export async function useFake() { const api = await import("../../api/client"); }'],
    ["a named re-export", 'export { stepUp } from "../../api/client";'],
    ["a re-export spelled with .js", 'export { stepUp as prove } from "../../api/session.js";'],
    ["export *", 'export * from "../../api/client";'],
    ["export * as", 'export * as api from "../../api/client";'],
    ["a local that shadows the import (fails closed)", `${DIRECT}function f(stepUp: () => void) { stepUp(); }`],
  ])("reports %s", (_, source) => {
    expect(flagged(source)).toBeGreaterThan(0);
  });

  it("allows api/client.ts, which is the API's public face", () => {
    expect(stepUpUses('export { stepUp } from "./session";', resolve(process.cwd(), "src/api/client.ts")))
      .toEqual({ sites: [], violations: [] });
  });
});

describe("productionStepUpUses", () => {
  // Seven in the Users hooks (disable and enable share one) and /connect's.
  it("finds every call site in the SPA, each under its own name, and no other use", () => {
    const { sites: found, violations } = productionStepUpUses();

    expect(violations).toEqual([]);
    expect(found.length).toBeGreaterThanOrEqual(8);
    expect(new Set(found).size).toBe(found.length);
  });
});
