// Self-tests for the CI vulnerability gate (#146). Run with
// `node --test .github/scripts/vuln-gate.test.mjs`.
//
// The cases that matter most are the ones where something MUST still block or
// MUST NOT suppress: an unusable report, an unknown severity, and an expired /
// malformed / newline-poisoned exception. Each of those asserts the fail-closed
// outcome (non-empty blocking, or an id that does NOT reach the allowlist), not
// just an absence.

import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import {
  advisoryId,
  emitAllowlist,
  exceptionProblem,
  extractJson,
  gate,
  isGhsaId,
  isValidException,
  levelProblem,
  loadExceptions,
  parseArgs,
  PARSERS,
  parseNpm,
  parseNuget,
  registryProblem,
  report,
  reportProblem,
  resolveParser,
  severityRank,
} from "./vuln-gate.mjs";

const NOW = new Date("2026-07-24T00:00:00Z");
const GHSA_ONE = "GHSA-aaaa-bbbb-cccc";
const GHSA_TWO = "GHSA-dddd-eeee-ffff";
const SCRIPT = fileURLToPath(new URL("./vuln-gate.mjs", import.meta.url));

const finding = (over = {}) => ({
  id: GHSA_ONE, package: "left-pad@1.0.0", severity: "high", title: "", url: "", ...over,
});
// A fully-valid exception; tests override one field at a time to isolate a rule.
const exception = (over = {}) => ({
  id: GHSA_ONE, ecosystem: "npm", reason: "no upstream fix — tracked in #000", expires: "2026-12-31", ...over,
});

test("severity ranks follow the shared npm/NuGet ladder", () => {
  assert.ok(severityRank("critical") > severityRank("high"));
  assert.ok(severityRank("high") > severityRank("moderate"));
  assert.ok(severityRank("moderate") > severityRank("low"));
  assert.equal(severityRank("HIGH"), severityRank("high")); // NuGet capitalises
});

test("an unknown or missing severity fails CLOSED — ranks above critical", () => {
  assert.ok(severityRank("nonsense") > severityRank("critical"));
  assert.ok(severityRank(undefined) > severityRank("critical"));
  assert.ok(severityRank("") > severityRank("critical"));
  // …so a finding with a severity we don't recognise still blocks at high.
  const result = gate({ findings: [finding({ severity: "sev-five" })], ecosystem: "npm", now: NOW });
  assert.equal(result.blocking.length, 1);
});

test("advisory ids come from the GHSA in the URL, with a fallback", () => {
  assert.equal(advisoryId(`https://github.com/advisories/${GHSA_ONE}`, 1234), GHSA_ONE);
  assert.equal(advisoryId("https://github.com/advisories/GHSA-AAAA-BBBB-CCCC", 1), GHSA_ONE);
  assert.equal(advisoryId("https://example.test/CVE-2026-1", 4242), "4242");
  assert.equal(advisoryId(undefined, undefined), "UNKNOWN");
});

test("isGhsaId accepts only an exact, whole GHSA id", () => {
  assert.ok(isGhsaId(GHSA_ONE));
  assert.ok(isGhsaId("ghsa-aaaa-bbbb-cccc")); // case-insensitive
  assert.ok(!isGhsaId(`${GHSA_ONE},${GHSA_TWO}`)); // no smuggled second id
  assert.ok(!isGhsaId(`${GHSA_ONE}\nghsas=${GHSA_TWO}`)); // no embedded newline
  assert.ok(!isGhsaId("GHSA-aaaa-bbbb-cccc-extra"));
  assert.ok(!isGhsaId("1099999"));
  assert.ok(!isGhsaId(undefined));
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

test("nuget: two DISTINCT non-GHSA advisories on one package stay distinct", () => {
  // Fallback identity is the advisory URL, not the coordinate — else dedupe
  // would collapse two different advisories on the same package into one.
  const findings = parseNuget({
    projects: [{
      frameworks: [{
        topLevelPackages: [{
          id: "Pkg", resolvedVersion: "1.0.0",
          vulnerabilities: [
            { severity: "High", advisoryurl: "https://example.test/CVE-2026-1" },
            { severity: "Critical", advisoryurl: "https://example.test/CVE-2026-2" },
          ],
        }],
      }],
    }],
  });
  assert.equal(findings.length, 2);
});

test("nuget: an advisory with no URL falls back to coordinates — and a GHSA exception can't mute it", () => {
  const findings = parseNuget({
    projects: [{ frameworks: [{ topLevelPackages: [{
      id: "Pkg", resolvedVersion: "1.0.0", vulnerabilities: [{ severity: "High" }],
    }] }] }],
  });
  assert.deepEqual(findings.map((f) => f.id), ["Pkg@1.0.0"]);
  // A GHSA-keyed exception cannot match a coordinate id, so it still blocks
  // (safe): non-GHSA advisories are documented as not exceptable.
  const result = gate({
    findings, exceptions: [exception({ ecosystem: "nuget" })], ecosystem: "nuget", now: NOW,
  });
  assert.equal(result.blocking.length, 1);
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
    findings: [finding()], exceptions: [exception({ reason: "no patch upstream" })], ecosystem: "npm", now: NOW,
  });
  assert.equal(result.blocking.length, 0);
  assert.deepEqual(result.suppressed.map((f) => f.reason), ["no patch upstream"]);
  assert.equal(result.staleExceptions.length, 0);
});

test("gate: an EXPIRED exception still blocks, and is reported as stale", () => {
  const result = gate({
    findings: [finding()], exceptions: [exception({ expires: "2026-07-23" })], ecosystem: "npm", now: NOW,
  });
  assert.equal(result.blocking.length, 1, "a lapsed exception must not keep muting the advisory");
  assert.deepEqual(result.staleExceptions.map((e) => e.id), [GHSA_ONE]);
});

test("gate: expiry is inclusive to the END of the named UTC day", () => {
  const findings = [finding()];
  const ex = [exception({ expires: "2026-07-24" })];
  // Midnight at the start of the 24th → still live.
  assert.equal(gate({ findings, exceptions: ex, ecosystem: "npm", now: new Date("2026-07-24T00:00:00Z") }).blocking.length, 0);
  // 23:59:59 on the 24th → still live.
  assert.equal(gate({ findings, exceptions: ex, ecosystem: "npm", now: new Date("2026-07-24T23:59:59Z") }).blocking.length, 0);
  // Midnight of the 25th → lapsed, blocks again.
  assert.equal(gate({ findings, exceptions: ex, ecosystem: "npm", now: new Date("2026-07-25T00:00:00Z") }).blocking.length, 1);
});

test("gate: an exception scoped to the other ecosystem does not apply", () => {
  const npmRun = gate({ findings: [finding()], exceptions: [exception({ ecosystem: "nuget" })], ecosystem: "npm", now: NOW });
  assert.equal(npmRun.blocking.length, 1, "a NuGet exception must not mute an npm advisory");
  assert.equal(npmRun.staleExceptions.length, 0, "nor should it be reported as stale on the npm run");

  const anyRun = gate({ findings: [finding()], exceptions: [exception({ ecosystem: "any" })], ecosystem: "npm", now: NOW });
  assert.equal(anyRun.blocking.length, 0);
});

test("gate: matching an id is case-insensitive", () => {
  const result = gate({
    findings: [finding()], exceptions: [exception({ id: GHSA_ONE.toLowerCase() })], ecosystem: "npm", now: NOW,
  });
  assert.equal(result.blocking.length, 0);
});

test("exceptionProblem: names why a malformed entry is rejected", () => {
  assert.equal(exceptionProblem(exception()), null); // the valid baseline
  assert.match(exceptionProblem(exception({ id: "1099999" })), /GHSA/);
  assert.match(exceptionProblem(exception({ id: `${GHSA_ONE},${GHSA_TWO}` })), /GHSA/);
  assert.match(exceptionProblem(exception({ ecosystem: undefined })), /ecosystem/);
  assert.match(exceptionProblem(exception({ ecosystem: "pypi" })), /ecosystem/);
  assert.match(exceptionProblem(exception({ reason: "  " })), /reason/);
  assert.match(exceptionProblem(exception({ expires: undefined })), /date/);
  assert.match(exceptionProblem(exception({ expires: "December 31, 2099" })), /YYYY-MM-DD/);
  assert.match(exceptionProblem(exception({ expires: "2026-02-30" })), /calendar/); // parseable but impossible
  assert.equal(exceptionProblem(null), "not an object");
  assert.ok(isValidException(exception()) && !isValidException(exception({ reason: "" })));
});

test("gate: a MALFORMED exception is ignored (never suppresses) and reported", () => {
  for (const bad of [
    exception({ expires: "December 31, 2099" }), // non-ISO but Date.parse-able
    exception({ expires: "2026-02-30" }),        // normalises to Mar 2 — must not suppress
    exception({ ecosystem: undefined }),         // no scope
    exception({ reason: "" }),                   // no justification
    exception({ id: `${GHSA_ONE},${GHSA_TWO}` }),// smuggled second id
  ]) {
    const result = gate({ findings: [finding()], exceptions: [bad], ecosystem: "npm", now: NOW });
    assert.equal(result.blocking.length, 1, `must still block: ${JSON.stringify(bad)}`);
    assert.equal(result.invalidExceptions.length, 1);
    assert.equal(result.suppressed.length, 0);
  }
});

// #634 — the registry is the one place an ecosystem is wired in, and the sites
// that used to hand-dispatch must all derive from it. The sharp edge the
// refactor removes: a two-way ternary that parsed any unknown ecosystem as
// NuGet. So the guard is not "npm and nuget still work" (true with the bug
// present) but "a half-wired or unknown ecosystem is REFUSED, never defaulted".
test("PARSERS: exactly the wired ecosystems, each carrying its own parse and shape check", () => {
  assert.deepEqual(Object.keys(PARSERS).sort(), ["npm", "nuget"]);
  assert.equal(PARSERS.npm.parse, parseNpm);
  assert.equal(PARSERS.nuget.parse, parseNuget);
  assert.equal(registryProblem(PARSERS), null);
  assert.ok(Object.isFrozen(PARSERS) && Object.isFrozen(PARSERS.npm) && Object.isFrozen(PARSERS.nuget));
});

test("registryProblem: an entry without a parser or a shape check is named, not tolerated", () => {
  assert.match(registryProblem({ npm: PARSERS.npm, pypi: {} }), /pypi.*parse/);
  assert.match(registryProblem({ npm: PARSERS.npm, pypi: { parse: () => [] } }), /pypi.*reportProblem/);
  assert.match(registryProblem({}), /no ecosystems/);
  assert.match(registryProblem(null), /not an object/);
});

test("resolveParser: a half-wired entry REFUSES to run instead of falling through to a default", () => {
  const halfWired = { npm: PARSERS.npm, nuget: PARSERS.nuget, pypi: {} };
  const picked = resolveParser("pypi", halfWired);
  assert.equal(picked.parser, undefined);
  assert.match(picked.problem, /pypi.*parse/);
  // The broken entry poisons the whole registry — even a good name is refused
  // until it is fixed, so a bad merge cannot half-work.
  assert.match(resolveParser("npm", halfWired).problem, /pypi/);
});

test("resolveParser: an unknown ecosystem is a problem naming the known ones; known ones resolve case-insensitively", () => {
  const unknown = resolveParser("pypi");
  assert.equal(unknown.parser, undefined);
  assert.match(unknown.problem, /unknown ecosystem "pypi"/);
  assert.match(unknown.problem, /npm\|nuget/);
  assert.equal(resolveParser("NPM").parser, PARSERS.npm);
  assert.equal(resolveParser("nuget").parser, PARSERS.nuget);
});

test("reportProblem: an ecosystem nothing can check is a problem, not a clean report", () => {
  assert.match(reportProblem({ projects: [] }, "pypi"), /unknown ecosystem/);
  assert.match(reportProblem({ vulnerabilities: {} }, ""), /unknown ecosystem/);
});

test("exceptionProblem: the ecosystem message lists the registry's names plus the 'any' scope", () => {
  assert.match(exceptionProblem(exception({ ecosystem: "pypi" })), /npm, nuget, any/);
  assert.equal(exceptionProblem(exception({ ecosystem: "any" })), null);
});

test("CLI: an unknown --ecosystem is a usage error naming the registry, not a run against a default parser", () => {
  const run = spawnSync(
    process.execPath,
    [SCRIPT, "--ecosystem", "pypi"],
    { cwd: process.cwd(), encoding: "utf8", input: JSON.stringify({ projects: [] }) },
  );
  assert.equal(run.status, 2);
  assert.match(run.stderr, /--ecosystem npm\|nuget/);
  assert.match(run.stderr, /unknown ecosystem "pypi"/);
});

test("reportProblem: an npm error payload is NOT clean — fails closed", () => {
  assert.match(reportProblem({ error: { code: "ENETUNREACH" } }, "npm"), /error/);
  assert.match(reportProblem({ metadata: {} }, "npm"), /auditReportVersion/); // neither key present
  assert.equal(reportProblem({ auditReportVersion: 2, vulnerabilities: {} }, "npm"), null);
  assert.equal(reportProblem({ vulnerabilities: {} }, "npm"), null); // vulnerabilities alone is fine
});

test("reportProblem: NuGet output without a projects array is unusable", () => {
  assert.match(reportProblem({ version: 1 }, "nuget"), /projects/);
  assert.equal(reportProblem({ version: 1, projects: [] }, "nuget"), null);
  assert.equal(reportProblem(null, "nuget"), "not a JSON object");
});

test("emitAllowlist: only valid, live GHSA ids, across ecosystems, as a comma list", () => {
  const list = emitAllowlist([
    exception({ id: GHSA_ONE, ecosystem: "npm" }),
    exception({ id: GHSA_TWO, ecosystem: "nuget" }), // both manifests count
    exception({ id: "GHSA-gggg-hhhh-iiii", ecosystem: "any", expires: "2026-07-23" }), // expired → out
    exception({ id: "1099999" }),                    // not a GHSA → out
    exception({ id: GHSA_ONE, expires: undefined }), // invalid (no expiry) → out
    exception({ id: GHSA_ONE, reason: "" }),         // invalid (no reason) → out
  ], NOW).split(",").filter(Boolean).sort();
  assert.deepEqual(list, [GHSA_ONE, GHSA_TWO].sort());
});

test("emitAllowlist: an adversarial id can never smuggle extra allowlist entries", () => {
  // Even though these carry a live, otherwise-valid-looking exception, the id
  // itself is not a bare GHSA, so nothing is emitted — no comma- or newline-
  // injected second id can reach dependency-review's allow-ghsas / GITHUB_OUTPUT.
  assert.equal(emitAllowlist([exception({ id: `${GHSA_ONE},${GHSA_TWO}` })], NOW), "");
  assert.equal(emitAllowlist([exception({ id: `${GHSA_ONE}\nghsas=${GHSA_TWO}` })], NOW), "");
  assert.equal(emitAllowlist([], NOW), "");
  assert.equal(emitAllowlist([exception({ expires: "2000-01-01" })], NOW), ""); // expired
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

test("levelProblem accepts supported case-insensitive thresholds and rejects bad values", () => {
  for (const level of ["info", "LOW", "Moderate", "high", "CRITICAL"]) {
    assert.equal(levelProblem(level), null);
  }
  assert.match(levelProblem("hihg"), /--level/);
  assert.match(levelProblem(), /--level/);
});

test("gate: rejects an unknown configured threshold instead of lowering the floor", () => {
  assert.throws(
    () => gate({ findings: [finding({ severity: "critical" })], ecosystem: "npm", level: "hihg", now: NOW }),
    /--level/,
  );
});

test("CLI: a critical npm advisory blocks by default and a typoed level is a usage error", () => {
  const report = JSON.stringify({
    auditReportVersion: 2,
    vulnerabilities: {
      "left-pad": {
        name: "left-pad",
        severity: "critical",
        via: [{
          source: 1,
          name: "left-pad",
          severity: "critical",
          title: "critical test advisory",
          url: `https://github.com/advisories/${GHSA_ONE}`,
        }],
      },
    },
  });
  const run = (level) => spawnSync(
    process.execPath,
    [SCRIPT, "--ecosystem", "npm", ...(level ? ["--level", level] : [])],
    { cwd: process.cwd(), encoding: "utf8", input: report },
  );

  assert.equal(run().status, 1);
  const invalid = run("hihg");
  assert.equal(invalid.status, 2);
  assert.match(invalid.stderr, /--level/);
});

test("gate: expired ANY and npm scopes are each reported stale", () => {
  for (const ecosystem of ["ANY", "any", "NPM", "npm"]) {
    const result = gate({
      findings: [finding()],
      exceptions: [exception({ ecosystem, expires: "2026-07-23" })],
      ecosystem: "npm",
      now: NOW,
    });
    assert.equal(result.blocking.length, 1, `${ecosystem} exception must not suppress after expiry`);
    assert.equal(result.staleExceptions.length, 1, `${ecosystem} exception must be reported stale`);
  }
});

test("loadExceptions: only a missing file is quiet; unusable files warn and suppress nothing", () => {
  const dir = mkdtempSync(join(tmpdir(), "cluckwork-vuln-gate-"));
  const path = join(dir, "exceptions.json");
  const warnings = [];
  const warn = (line) => warnings.push(line);

  try {
    assert.deepEqual(loadExceptions(path, warn), []);
    assert.deepEqual(warnings, []);

    warnings.length = 0;
    assert.deepEqual(warnings, []);
    assert.deepEqual(loadExceptions(dir, warn), []);
    assert.equal(warnings.length, 1);
    assert.ok(warnings[0].includes(dir));

    warnings.length = 0;
    writeFileSync(path, "not json", "utf8");
    assert.deepEqual(loadExceptions(path, warn), []);
    assert.equal(warnings.length, 1);
    assert.ok(warnings[0].includes(path));

    warnings.length = 0;
    writeFileSync(path, JSON.stringify({ exceptions: {} }), "utf8");
    assert.deepEqual(loadExceptions(path, warn), []);
    assert.equal(warnings.length, 1);
    assert.match(warnings[0], /array/i);

    warnings.length = 0;
    const valid = [exception()];
    writeFileSync(path, JSON.stringify({ exceptions: valid }), "utf8");
    assert.deepEqual(loadExceptions(path, warn), valid);
    assert.deepEqual(warnings, []);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// #686 — the ADVISORY run must never block, INCLUDING when the audit report is
// unusable. npm's advisory endpoint returning 503 emits an error payload; the
// blocking gate must still fail closed on that ("an error payload is not 'no
// vulnerabilities'"), but the advisory step is documented in this file's header
// as `--warn-only  (report, always exit 0)` and in ci.yml as "ADVISORY only
// (never blocks)". It was doing neither: both bad-input paths set exit 2
// without consulting warnOnly, so a third-party outage failed CI on PRs that
// touched no web/ file at all.
//
// Both paths are covered, because they are separate returns in main(): an
// unusable-but-parseable report (reportProblem) and input that is not JSON at
// all (extractJson).
test("bad input: the advisory run warns and exits 0, the blocking run still fails closed", () => {
  const errorPayload = JSON.stringify({ error: { code: "E503", summary: "", detail: "" } });
  const notJson = "npm error audit endpoint returned an error";

  const run = (input, extra = []) => spawnSync(
    process.execPath,
    [SCRIPT, "--ecosystem", "npm", ...extra],
    { cwd: process.cwd(), encoding: "utf8", input },
  );

  // Unusable report — the npm-503 shape.
  const blocking = run(errorPayload);
  assert.equal(blocking.status, 2, "blocking gate must fail closed on an unusable report");
  assert.match(blocking.stderr, /::error::/);

  const advisory = run(errorPayload, ["--warn-only"]);
  assert.equal(advisory.status, 0, "advisory gate must not block on an unusable report");
  assert.match(advisory.stderr, /::warning::/);
  assert.doesNotMatch(advisory.stderr, /::error::/);

  // Unparseable input — the same rule applies on the other bad-input path.
  const blockingUnparseable = run(notJson);
  assert.equal(blockingUnparseable.status, 2, "blocking gate must fail closed on unparseable input");

  const advisoryUnparseable = run(notJson, ["--warn-only"]);
  assert.equal(advisoryUnparseable.status, 0, "advisory gate must not block on unparseable input");
  assert.match(advisoryUnparseable.stderr, /::warning::/);

  // The advisory run must still SAY something — a silent exit 0 would be a gate
  // nobody can tell ran at all.
  assert.match(advisory.stderr, /unusable audit report|could not parse/);
});
