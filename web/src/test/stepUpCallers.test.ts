import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { productionStepUpCallSites, stepUpCallSites } from "./stepUpCallers";

// #1279 — the walk's own behaviour, pinned against synthetic sources, so a
// caller written in a shape the real tree lacks today is still found.
const FILE = resolve(process.cwd(), "src/routes/users/useFake.ts");
const sites = (source: string) => stepUpCallSites(source, FILE);

describe("stepUpCallSites", () => {
  it("finds a call through an aliased import", () => {
    expect(sites(`
      import { stepUp as prove } from "../../api/client";
      export function useFake() { async function onSave() { await prove({ password: "" }); } }
    `)).toEqual(["useFake.onSave"]);
  });

  it("finds a member call on a namespace import, dotted or computed", () => {
    expect(sites(`
      import * as api from "../../api/session";
      export function useFake() {
        async function one() { await api.stepUp({ password: "" }); }
        async function two() { await api["stepUp"]({ password: "" }); }
      }
    `)).toEqual(["useFake.one", "useFake.two"]);
  });

  it("finds a call written with odd spacing", () => {
    expect(sites(`
      import { stepUp } from "../../api/client";
      export function useFake() { async function onSave() { await stepUp  (\n{ password: "" }); } }
    `)).toEqual(["useFake.onSave"]);
  });

  it("reports two calls in one function twice", () => {
    expect(sites(`
      import { stepUp } from "../../api/client";
      export function useFake() { async function onSave() { await stepUp({ password: "" }); await stepUp({ password: "" }); } }
    `)).toEqual(["useFake.onSave", "useFake.onSave"]);
  });

  it("names a function-valued const", () => {
    expect(sites(`
      import { stepUp } from "../../api/client";
      export const useFake = () => { const onSave = async () => { await stepUp({ password: "" }); }; };
    `)).toEqual(["useFake.onSave"]);
  });

  it("ignores a stepUp that is not the API's", () => {
    expect(sites(`
      import { stepUp } from "./elsewhere";
      export function useFake() { stepUp(); }
    `)).toEqual([]);
  });
});

describe("productionStepUpCallSites", () => {
  // Seven in the Users hooks (disable and enable share one) and /connect's.
  it("finds every call site in the SPA, each under its own name", () => {
    const found = productionStepUpCallSites();

    expect(found.length).toBeGreaterThanOrEqual(8);
    expect(new Set(found).size).toBe(found.length);
  });
});
