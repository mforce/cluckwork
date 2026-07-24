// Self-tests for the CI vulnerability gate (#146). Run with `node --test .github/scripts/`.
//
// A gate that silently passes is worse than no gate, so the cases that matter
// most here are the ones where something MUST still block: an expired exception,
// an exception with no end date, and an exception written for the other
// ecosystem. Each of those asserts a non-empty `blocking`, not just an absence.

import test from "node:test";
import assert from "node:assert/strict";
import {
  advisoryId,
  emitAllowlist,
  extractJson,
  gate,
  parseArgs,
  parseNpm,
  parseNuget,
  report,
  severityRank,
} from "./vuln-gate.mjs";

const NOW = new Date("2026-07-24T00:00:00Z");
const GHSA_ONE = "GHSA-aaaa-bbbb-cccc";
const GHSA_TWO = "GHSA-dddd-eeee-ffff";

const finding = (over = {}) => ({
  id: GHSA_ONE, package: "left-pad@1.0.0", severity: "high", title: "", url: "", ...over,
});

test("severity ranks follow the shared npm/NuGet ladder", () => {
  assert.ok(severityRank("critical") > severityRank("high"));
  assert.ok(severityRank("high") > severityRank("moderate"));
  assert.ok(severityRank("moderate") > severityRank("low"));
  assert.equal(severityRank("HIGH"), severityRank("high")); // NuGet capitalises
  assert.equal(severityRank("nonsense"), 0);
  assert.equal(severityRank(undefined), 0);
});

test("advisory ids come from the GHSA in the URL, with a fallback", () => {
  assert.equal(advisoryId(`https://github.com/advisories/${GHSA_ONE}`, 1234), GHSA_ONE);
  assert.equal(advisoryId("https://github.com/advisories/GHSA-AAAA-BBBB-CCCC", 1), GHSA_ONE);
  assert.equal(advisoryId("https://example.test/CVE-2026-1", 4242), "4242");
  assert.equal(advisoryId(undefined, undefined), "UNKNOWN");
});

test("npm: advisory objects become findings, 'via' strings do not", () => {
  const findings = parseNpm({
    vulnerabilities: {
      inner: {
        name: "inner", severity: "high",
        via: [{ source: 1, name: "inner", title: "RCE", url: `https://github.com/advisories/${GHSA_ONE}`, severity: "high" }],
      },
      // Vulnerable only *through* `inner` — the advisory is already counted above.
      outer: { name: "outer", severity: "high", via: ["inner"] },
    },
  });
  assert.deepEqual(findings.map((f) => f.id), [GHSA_ONE]);
  assert.equal(findings[0].package, "inner");
});

test("npm: the same advisory on the same package collapses to its worst severity", () => {
  const via = (severity) => ({ source: 1, name: "p", url: `https://github.com/advisories/${GHSA_ONE}`, severity });
  const findings = parseNpm({ vulnerabilities: { p: { name: "p", via: [via("moderate"), via("critical")] } } });
  assert.equal(findings.length, 1);
  assert.equal(findings[0].severity, "critical");
});

test("npm: an empty report yields no findings", () => {
  assert.deepEqual(parseNpm({ vulnerabilities: {}, metadata: {} }), []);
  assert.deepEqual(parseNpm({}), []);
});

test("nuget: clean projects carry no 'frameworks' key at all", () => {
  assert.deepEqual(parseNuget({ version: 1, projects: [{ path: "/a.csproj" }, { path: "/b.csproj" }] }), []);
});

test("nuget: both top-level and transitive packages are gated", () => {
  const findings = parseNuget({
    projects: [{
      path: "/a.csproj",
      frameworks: [{
        framework: "net10.0",
        topLevelPackages: [{
          id: "Direct", resolvedVersion: "1.0.0",
          vulnerabilities: [{ severity: "High", advisoryurl: `https://github.com/advisories/${GHSA_ONE}` }],
        }],
        transitivePackages: [{
          id: "Transitive", resolvedVersion: "2.0.0",
          // camelCase spelling accepted too, in case NuGet ever normalises the key
          vulnerabilities: [{ severity: "Critical", advisoryUrl: `https://github.com/advisories/${GHSA_TWO}` }],
        }],
      }],
    }],
  });
  assert.deepEqual(
    findings.map((f) => [f.package, f.severity, f.title]).sort(),
    [["Direct@1.0.0", "high", "direct"], ["Transitive@2.0.0", "critical", "transitive"]].sort(),
  );
});

test("gate: blocks at or above the level and ignores what is below it", () => {
  const result = gate({
    findings: [finding({ severity: "critical" }), finding({ id: GHSA_TWO, severity: "moderate" })],
    ecosystem: "npm", now: NOW,
  });
  assert.deepEqual(result.blocking.map((f) => f.id), [GHSA_ONE]);
  assert.equal(result.belowLevel, 1);

  const lowered = gate({ findings: [finding({ severity: "moderate" })], level: "moderate", ecosystem: "npm", now: NOW });
  assert.equal(lowered.blocking.length, 1);
});

test("gate: a live exception suppresses, carrying its reason forward", () => {
  const result = gate({
    findings: [finding()],
    exceptions: [{ id: GHSA_ONE, ecosystem: "npm", reason: "no patch upstream", expires: "2026-12-31" }],
    ecosystem: "npm", now: NOW,
  });
  assert.equal(result.blocking.length, 0);
  assert.deepEqual(result.suppressed.map((f) => f.reason), ["no patch upstream"]);
  assert.equal(result.staleExceptions.length, 0);
});

test("gate: an EXPIRED exception still blocks, and is reported as stale", () => {
  const result = gate({
    findings: [finding()],
    exceptions: [{ id: GHSA_ONE, ecosystem: "npm", reason: "was unfixable", expires: "2026-07-23" }],
    ecosystem: "npm", now: NOW,
  });
  assert.equal(result.blocking.length, 1, "a lapsed exception must not keep muting the advisory");
  assert.deepEqual(result.staleExceptions.map((e) => e.id), [GHSA_ONE]);
});

test("gate: an exception with no expiry never suppresses", () => {
  for (const expires of [undefined, "", "whenever"]) {
    const result = gate({
      findings: [finding()],
      exceptions: [{ id: GHSA_ONE, ecosystem: "npm", reason: "forever", expires }],
      ecosystem: "npm", now: NOW,
    });
    assert.equal(result.blocking.length, 1, `expires=${String(expires)} must not suppress`);
  }
});

test("gate: an exception scoped to the other ecosystem does not apply", () => {
  const live = { id: GHSA_ONE, reason: "nuget only", expires: "2026-12-31" };
  const npmRun = gate({ findings: [finding()], exceptions: [{ ...live, ecosystem: "nuget" }], ecosystem: "npm", now: NOW });
  assert.equal(npmRun.blocking.length, 1, "a NuGet exception must not mute an npm advisory");
  assert.equal(npmRun.staleExceptions.length, 0, "nor should it be reported as stale on the npm run");

  const anyRun = gate({ findings: [finding()], exceptions: [{ ...live, ecosystem: "any" }], ecosystem: "npm", now: NOW });
  assert.equal(anyRun.blocking.length, 0);
});

test("gate: matching an id is case-insensitive", () => {
  const result = gate({
    findings: [finding()],
    exceptions: [{ id: GHSA_ONE.toLowerCase(), ecosystem: "npm", reason: "r", expires: "2026-12-31" }],
    ecosystem: "npm", now: NOW,
  });
  assert.equal(result.blocking.length, 0);
});

test("emitAllowlist: only live GHSA-shaped ids, across ecosystems, as a comma list", () => {
  const exceptions = [
    { id: GHSA_ONE, ecosystem: "npm", expires: "2026-12-31" },
    { id: GHSA_TWO, ecosystem: "nuget", expires: "2026-12-31" }, // both manifests count
    { id: "GHSA-gggg-hhhh-iiii", ecosystem: "any", expires: "2026-07-23" }, // expired → out
    { id: "1099999", ecosystem: "npm", expires: "2026-12-31" }, // numeric npm id → not expressible
    { id: GHSA_ONE, ecosystem: "npm" }, // no expiry → out
  ];
  const list = emitAllowlist(exceptions, NOW).split(",").filter(Boolean).sort();
  assert.deepEqual(list, [GHSA_ONE, GHSA_TWO].sort());
});

test("emitAllowlist: nothing live yields an empty string, not 'undefined'", () => {
  assert.equal(emitAllowlist([], NOW), "");
  assert.equal(emitAllowlist([{ id: GHSA_ONE, expires: "2000-01-01" }], NOW), "");
});

test("report: blocking findings are errors, or warnings in advisory mode", () => {
  const result = gate({ findings: [finding()], ecosystem: "npm", now: NOW });
  const lines = [];
  report({ result, ecosystem: "npm", level: "high", warnOnly: false, log: (l) => lines.push(l) });
  assert.ok(lines.some((l) => l.startsWith("::error::")));

  const advisory = [];
  report({ result, ecosystem: "npm", level: "high", warnOnly: true, log: (l) => advisory.push(l) });
  assert.ok(advisory.some((l) => l.startsWith("::warning::")));
  assert.ok(!advisory.some((l) => l.startsWith("::error::")));
});

test("extractJson skips CLI chatter ahead of the document", () => {
  assert.deepEqual(extractJson('Determining projects to restore...\n{"version":1}'), { version: 1 });
  assert.throws(() => extractJson("nothing here"), /no JSON object/);
});

test("parseArgs reads the flags and keeps sensible defaults", () => {
  assert.deepEqual(parseArgs(["--ecosystem", "NuGet"]), {
    ecosystem: "nuget", level: "high", warnOnly: false, emitAllowlist: false,
    exceptionsPath: ".github/security-exceptions.json",
  });
  const parsed = parseArgs(["--ecosystem", "npm", "--level", "Moderate", "--warn-only", "--exceptions", "x.json"]);
  assert.deepEqual(parsed, {
    ecosystem: "npm", level: "moderate", warnOnly: true, emitAllowlist: false, exceptionsPath: "x.json",
  });
  assert.equal(parseArgs(["--emit-allowlist"]).emitAllowlist, true);
});
