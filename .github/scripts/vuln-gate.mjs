#!/usr/bin/env node
// Dependency-vulnerability gate for CI (#146).
//
// Why a script and not the stock commands:
//   - `npm audit --audit-level=high` gates, but has NO allowlist. One unfixable
//     upstream advisory would block every unrelated PR until it is patched.
//   - `dotnet list package --vulnerable` ALWAYS exits 0, so on its own it cannot
//     gate anything — its output has to be parsed either way.
// Both sides need parsing, so parsing once buys a single exceptions file, one
// severity ladder, and one report format across the two ecosystems.
//
// Posture: FAIL CLOSED. Unreadable input, an unrecognised report shape, an
// unknown severity, or a malformed exception all err toward blocking — a gate
// that passes when it cannot tell is worse than no gate.
//
// Usage:
//   npm audit --json --omit=dev | node .github/scripts/vuln-gate.mjs --ecosystem npm
//   dotnet list package --vulnerable --include-transitive --format json --output-version 1 \
//     | node .github/scripts/vuln-gate.mjs --ecosystem nuget
//
// Flags: --level <info|low|moderate|high|critical>  (default high — blocks at or above)
//        --warn-only                           (report, always exit 0)
//        --exceptions <path>                   (default .github/security-exceptions.json)
//        --emit-allowlist                      (print live GHSA ids for dependency-review)
//
// Exit 0 = clean, fully excepted, or ANY outcome under --warn-only; exit 1 =
// blocking advisory; exit 2 = bad input, unless --warn-only (see badInput).

import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

const SEVERITIES = ["info", "low", "moderate", "high", "critical"];
// One rung above `critical`, so anything we don't recognise sorts ABOVE the
// highest known severity and therefore blocks at any threshold (fail closed).
const UNKNOWN_RANK = SEVERITIES.length;
const ECOSYSTEMS = new Set(["npm", "nuget", "any"]);
const ONE_DAY_MS = 86_400_000;

// A finding's severity comes from a tool we don't control: an unrecognised or
// missing value must not silently rank below the gate's floor and slip through.
// (`level`, by contrast, is always one of our own known strings.)
export function severityRank(severity) {
  const i = SEVERITIES.indexOf(String(severity ?? "").toLowerCase());
  return i < 0 ? UNKNOWN_RANK : i;
}

export function levelProblem(level) {
  return SEVERITIES.includes(String(level ?? "").toLowerCase())
    ? null
    : `--level must be one of ${SEVERITIES.join(", ")}`;
}

// Both ecosystems point their advisory URLs at GitHub Security Advisories, so a
// GHSA id is the one key that means the same thing on both sides — that is what
// an exception is written against. Numeric npm ids / package coordinates are
// only a fallback for an advisory published somewhere else.
const GHSA_ANYWHERE = /(GHSA-[0-9a-z]{4}-[0-9a-z]{4}-[0-9a-z]{4})/i;
// Fully anchored: an exception id / allowlist entry must be EXACTLY a GHSA and
// nothing else. Without the anchors a value like "GHSA-aaaa-bbbb-cccc,GHSA-…"
// or one carrying an embedded newline would pass a prefix check and then, once
// interpolated into dependency-review's comma-separated `allow-ghsas` or into
// `$GITHUB_OUTPUT`, smuggle in extra ids — forging the allowlist (#146 review).
const GHSA_EXACT = /^GHSA-[0-9a-z]{4}-[0-9a-z]{4}-[0-9a-z]{4}$/i;

export function isGhsaId(value) {
  return typeof value === "string" && GHSA_EXACT.test(value);
}

function canonicalGhsa(value) {
  return `GHSA-${value.slice(5).toLowerCase()}`;
}

export function advisoryId(url, fallback) {
  const match = typeof url === "string" ? url.match(GHSA_ANYWHERE) : null;
  // Canonical GitHub form: uppercase prefix, lowercase body — so an id pasted
  // straight from a GitHub advisory page is what appears in the log.
  return match ? canonicalGhsa(match[1]) : String(fallback ?? "UNKNOWN");
}

function dedupe(findings) {
  const seen = new Map();
  for (const f of findings) {
    const key = `${f.id}|${f.package}`;
    // Keep the worst severity reported for the same advisory/package pair.
    const prev = seen.get(key);
    if (!prev || severityRank(f.severity) > severityRank(prev.severity)) seen.set(key, f);
  }
  return [...seen.values()];
}

// `npm audit --json` (auditReportVersion 2). Each vulnerabilities[name].via entry
// is either an advisory object or a bare package name — the latter means "this
// package is vulnerable *through* that one". The advisory OBJECT is always
// carried on the entry of the package the advisory is published against (which
// npm audit always includes in the map), so iterating every entry sees each
// advisory exactly once; a string via would only double-count. Verified against
// a real transitive advisory (mkdirp@0.5.1 → minimist), #146 review.
export function parseNpm(report) {
  const findings = [];
  for (const entry of Object.values(report?.vulnerabilities ?? {})) {
    for (const via of entry?.via ?? []) {
      if (typeof via !== "object" || via === null) continue;
      findings.push({
        id: advisoryId(via.url, via.source),
        package: via.name ?? entry.name ?? "unknown",
        severity: String(via.severity ?? entry.severity ?? "").toLowerCase(),
        title: via.title ?? "",
        url: via.url ?? "",
      });
    }
  }
  return dedupe(findings);
}

// `dotnet list package --vulnerable --include-transitive --format json`. A clean
// project has no `frameworks` key at all, so every level is optional. NuGet spells
// the advisory link `advisoryurl` (all lower case); accept the camelCase spelling
// too in case that is ever normalised.
export function parseNuget(report) {
  const findings = [];
  for (const project of report?.projects ?? []) {
    for (const framework of project?.frameworks ?? []) {
      for (const kind of ["topLevelPackages", "transitivePackages"]) {
        for (const pkg of framework?.[kind] ?? []) {
          for (const vuln of pkg?.vulnerabilities ?? []) {
            const url = vuln.advisoryurl ?? vuln.advisoryUrl ?? "";
            const coordinates = `${pkg.id}@${pkg.resolvedVersion ?? "?"}`;
            findings.push({
              // Prefer the GHSA; else fall back to the advisory URL so two
              // distinct non-GHSA advisories on the SAME package stay distinct
              // through dedupe (coordinates alone would collapse them).
              id: advisoryId(url, url || coordinates),
              package: coordinates,
              severity: String(vuln.severity ?? "").toLowerCase(),
              title: kind === "transitivePackages" ? "transitive" : "direct",
              url,
            });
          }
        }
      }
    }
  }
  return dedupe(findings);
}

// An exception is a deliberate, dated, single-advisory mute. Every field is
// validated before it can suppress anything; a malformed entry is IGNORED (it
// never suppresses) and surfaced as a warning. This is what stops a typo'd or
// hand-crafted entry — `Date.parse("December 31, 2099")`, an impossible
// `2026-02-30`, a comma/newline-bearing id, a missing scope — from opening a
// hole (#146 review).
export function exceptionProblem(exception) {
  if (typeof exception !== "object" || exception === null) return "not an object";
  if (!isGhsaId(exception.id)) return "id is not a canonical GHSA (GHSA-xxxx-xxxx-xxxx)";
  if (!ECOSYSTEMS.has(String(exception.ecosystem ?? "").toLowerCase()))
    return "ecosystem must be one of npm, nuget, any";
  if (typeof exception.reason !== "string" || exception.reason.trim() === "")
    return "reason must be a non-empty string";
  // Strict calendar date: the literal must be YYYY-MM-DD *and* round-trip, so a
  // normalised-away value like 2026-02-30 (→ Mar 2) is rejected, not accepted.
  const expires = String(exception.expires ?? "");
  if (!/^\d{4}-\d{2}-\d{2}$/.test(expires)) return "expires must be a YYYY-MM-DD date";
  const parsed = new Date(`${expires}T00:00:00Z`);
  if (Number.isNaN(parsed.getTime()) || parsed.toISOString().slice(0, 10) !== expires)
    return `expires is not a real calendar date (${expires})`;
  return null;
}

export function isValidException(exception) {
  return exceptionProblem(exception) === null;
}

// Live = valid AND within its window. The window runs to the END of the named
// UTC day (inclusive), so `expires: 2026-07-24` still suppresses throughout the
// 24th rather than lapsing at that day's first instant (#146 review). Callers
// pass only already-validated exceptions.
function isLive(exception, now) {
  const dayStart = Date.parse(`${exception.expires}T00:00:00Z`);
  return Number.isFinite(dayStart) && now.getTime() < dayStart + ONE_DAY_MS;
}

function scopeOf(exception) {
  return String(exception.ecosystem ?? "").toLowerCase();
}

function inScope(exception, ecosystem) {
  const scope = scopeOf(exception);
  return scope === "any" || scope === String(ecosystem).toLowerCase();
}

function matches(exception, finding, ecosystem) {
  if (!inScope(exception, ecosystem)) return false;
  return canonicalGhsa(exception.id) === finding.id;
}

export function gate({ findings, exceptions = [], ecosystem, level = "high", now = new Date() }) {
  const problem = levelProblem(level);
  if (problem) throw new Error(problem);
  const floor = SEVERITIES.indexOf(String(level).toLowerCase());
  const atOrAbove = findings.filter((f) => severityRank(f.severity) >= floor);

  const valid = exceptions.filter(isValidException);
  // A malformed entry is reported (so it gets fixed) but NEVER suppresses.
  const invalidExceptions = exceptions
    .filter((e) => !isValidException(e))
    .map((e) => ({ raw: e, problem: exceptionProblem(e) }));

  const suppressed = [];
  const blocking = [];
  for (const finding of atOrAbove) {
    const excuse = valid.find((e) => matches(e, finding, ecosystem) && isLive(e, now));
    if (excuse) suppressed.push({ ...finding, reason: excuse.reason, expires: excuse.expires });
    else blocking.push(finding);
  }

  // Valid, in-scope, but past its window — surfaced so a lapsed entry gets
  // deleted instead of lingering as dead config.
  const staleExceptions = valid.filter((e) => inScope(e, ecosystem) && !isLive(e, now));

  return {
    blocking,
    suppressed,
    staleExceptions,
    invalidExceptions,
    belowLevel: findings.length - atOrAbove.length,
  };
}

// The live (unexpired) GHSA ids, for actions/dependency-review-action's
// `allow-ghsas` input — so the diff-scoped PR gate honours the very same
// exceptions file as the tree-scoped audit gates, from one source of truth.
// Only VALID exceptions qualify, and each id is re-emitted in canonical form, so
// the output is guaranteed to be a comma-list of bare GHSA ids — no separators
// or newlines that could forge extra allowlist/`$GITHUB_OUTPUT` entries.
export function emitAllowlist(exceptions, now = new Date()) {
  return exceptions
    .filter((e) => isValidException(e) && isLive(e, now))
    .map((e) => canonicalGhsa(e.id))
    .join(",");
}

function describe(finding) {
  const bits = [finding.severity, finding.package, finding.id, finding.title, finding.url];
  return bits.filter(Boolean).join(" — ");
}

export function report({ result, ecosystem, level, warnOnly, log = console.log }) {
  const kind = warnOnly ? "warning" : "error";
  for (const finding of result.blocking) log(`::${kind}::[${ecosystem}] ${describe(finding)}`);
  for (const finding of result.suppressed)
    log(`::notice::[${ecosystem}] excepted until ${finding.expires}: ${describe(finding)} — ${finding.reason}`);
  for (const stale of result.staleExceptions)
    log(`::warning::[${ecosystem}] exception ${stale.id} lapsed (${stale.expires ?? "no expiry"}) — remove it from .github/security-exceptions.json`);
  for (const { raw, problem } of result.invalidExceptions ?? [])
    log(`::warning::[${ecosystem}] ignored malformed exception (${problem}): ${JSON.stringify(raw)}`);

  if (result.blocking.length === 0)
    log(`[${ecosystem}] no advisories at or above "${level}" (${result.suppressed.length} excepted, ${result.belowLevel} below threshold).`);
  else
    log(`[${ecosystem}] ${result.blocking.length} advisor${result.blocking.length === 1 ? "y" : "ies"} at or above "${level}"${warnOnly ? " (advisory only — not blocking)" : ""}.`);
}

export function parseArgs(argv) {
  const options = {
    ecosystem: "",
    level: "high",
    warnOnly: false,
    emitAllowlist: false,
    exceptionsPath: ".github/security-exceptions.json",
  };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--ecosystem") options.ecosystem = String(argv[++i] ?? "").toLowerCase();
    else if (argv[i] === "--level") options.level = String(argv[++i] ?? "").toLowerCase();
    else if (argv[i] === "--warn-only") options.warnOnly = true;
    else if (argv[i] === "--emit-allowlist") options.emitAllowlist = true;
    else if (argv[i] === "--exceptions") options.exceptionsPath = String(argv[++i] ?? "");
  }
  return options;
}

// The dotnet CLI can print restore chatter ahead of the JSON document, so start
// at the first brace rather than trusting the stream to be pure JSON.
export function extractJson(text) {
  const start = text.indexOf("{");
  if (start < 0) throw new Error("no JSON object found in input");
  return JSON.parse(text.slice(start));
}

// Validate the report is the SHAPE we expect before trusting an empty finding
// list. `npm audit` is network-backed and, on a registry/auth failure, prints an
// `{ "error": … }` payload with no `vulnerabilities` and a non-zero exit — which
// the workflow's `|| true` swallows. Without this check that would parse as
// "clean" and pass the gate (fail OPEN). Likewise a NuGet document with no
// `projects` array is not a real run (#146 review).
export function reportProblem(report, ecosystem) {
  if (typeof report !== "object" || report === null) return "not a JSON object";
  if (ecosystem === "npm") {
    if (report.error) return `npm audit reported an error: ${JSON.stringify(report.error)}`;
    if (report.auditReportVersion === undefined && report.vulnerabilities === undefined)
      return "npm audit output has neither auditReportVersion nor vulnerabilities";
  } else if (ecosystem === "nuget") {
    if (!Array.isArray(report.projects)) return "dotnet output has no projects array";
  }
  return null;
}

async function readStdin() {
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  return Buffer.concat(chunks).toString("utf8");
}

export function loadExceptions(path, warn = console.error) {
  let parsed;
  try {
    parsed = JSON.parse(readFileSync(path, "utf8"));
  } catch (err) {
    if (err?.code === "ENOENT") return []; // no file → empty allowlist, the normal case
    warn(`::warning::could not load security exceptions from ${path}: ${err.message}`);
    return [];
  }

  const exceptions = parsed?.exceptions;
  if (exceptions === undefined || exceptions === null) return [];
  if (!Array.isArray(exceptions)) {
    warn(`::warning::security exceptions from ${path} must contain an exceptions array`);
    return [];
  }
  return exceptions;
}

// #686 — the one place that decides what bad input costs. `--warn-only` is
// documented in this file's header as "report, always exit 0" and ci.yml
// describes the advisory step as "ADVISORY only (never blocks)"; both bad-input
// paths used to ignore that and exit 2, so an npm outage failed CI on PRs that
// touched nothing. The BLOCKING gate keeps failing closed — that is #146 and it
// does not change here.
export function badInput(options, message) {
  const prefix = `[${options.ecosystem}] ${message}`;
  if (options.warnOnly) {
    console.error(`::warning::${prefix} — advisory run, not blocking`);
    return 0;
  }
  console.error(`::error::${prefix}`);
  process.exitCode = 2;
  return 2;
}

async function main() {
  const options = parseArgs(process.argv.slice(2));

  // Allowlist mode: print the live GHSA ids for dependency-review and exit. No
  // stdin, no ecosystem — dependency-review spans both manifests at once.
  if (options.emitAllowlist) {
    process.stdout.write(emitAllowlist(loadExceptions(options.exceptionsPath)));
    return;
  }

  if (options.ecosystem !== "npm" && options.ecosystem !== "nuget") {
    console.error("usage: vuln-gate.mjs --ecosystem npm|nuget [--level high] [--warn-only]");
    process.exitCode = 2;
    return;
  }

  const problem = levelProblem(options.level);
  if (problem) {
    console.error(`usage: vuln-gate.mjs --ecosystem npm|nuget [--level info|low|moderate|high|critical] [--warn-only] — ${problem}`);
    process.exitCode = 2;
    return;
  }

  let parsed;
  try {
    parsed = extractJson(await readStdin());
  } catch (err) {
    // #686 — fail closed, EXCEPT under --warn-only. The blocking gate must not
    // pass when it cannot read its input; the advisory run cannot block even on
    // a finding, so blocking it on unreadable input is stranger still, and it
    // failed CI on unrelated PRs whenever npm's advisory endpoint was down.
    // Either way it says so: a silent exit 0 is a gate nobody can tell ran.
    badInput(options, `could not parse the audit output: ${err.message}`);
    return;
  }

  const shapeProblem = reportProblem(parsed, options.ecosystem);
  if (shapeProblem) {
    // #686 — same rule as the parse failure above: an error payload is not "no
    // vulnerabilities" for the blocking gate, and is not a build failure for the
    // advisory one.
    badInput(options, `unusable audit report — ${shapeProblem}`);
    return;
  }

  const exceptions = loadExceptions(options.exceptionsPath);
  const findings = options.ecosystem === "npm" ? parseNpm(parsed) : parseNuget(parsed);
  const result = gate({ findings, exceptions, ecosystem: options.ecosystem, level: options.level });
  report({ result, ecosystem: options.ecosystem, level: options.level, warnOnly: options.warnOnly });

  if (result.blocking.length > 0 && !options.warnOnly) process.exitCode = 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
