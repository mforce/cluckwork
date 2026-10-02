// Self-tests for the weekly image scan's issue logic. Run with
// `node --test .github/scripts/image-scan-issues.test.mjs`.
//
// The cases that matter: an issue that exists is updated, never duplicated; a
// cleared id is closed only on a completed scan of both architectures; and a
// human issue is never closed, whatever its body says.

import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

import { LABELS, buildTitle, collect, parseReport, plan, scannerIssueId } from "./image-scan-issues.mjs";

const SHA = "a".repeat(40);
const REPO = "ghcr.io/o/r";
const IMAGE = `${REPO}:sha-${SHA}`;
const DIGEST = { amd64: `sha256:${"a".repeat(64)}`, arm64: `sha256:${"b".repeat(64)}` };
const CTX = { sha: SHA, image: IMAGE, runUrl: "https://example.invalid/run", digests: DIGEST };
const CLI = fileURLToPath(new URL("./image-scan-issues.mjs", import.meta.url));

const vuln = (id, pkg, severity = "HIGH") => ({
  VulnerabilityID: id,
  PkgName: pkg,
  InstalledVersion: "1",
  FixedVersion: "2",
  Severity: severity,
  Title: "a title",
  PrimaryURL: "https://example.invalid/" + id,
});

const report = (arch, vulns, over = {}) =>
  JSON.stringify({
    SchemaVersion: 2,
    ArtifactType: "container_image",
    ArtifactName: `${REPO}@${DIGEST[arch]}`,
    Metadata: { OS: { Family: "ubuntu" }, ImageConfig: { architecture: arch } },
    Results: [{ Vulnerabilities: vulns }],
    ...over,
  });

const parsed = (vulnsByArch) =>
  Object.fromEntries(Object.entries(vulnsByArch).map(([arch, v]) => [arch, parseReport(report(arch, v), { repo: REPO, arch })]));

const issue = (number, title, labels = LABELS, comments = [], author = "github-actions[bot]") => ({ number, title, labels, comments, author });

const openCve = collect(parsed({ amd64: [vuln("CVE-2026-1111", "openssl")], arm64: [vuln("CVE-2026-1111", "openssl")] }));
const clean = collect(parsed({ amd64: [], arm64: [] }));
const titleFor = (findings, id) => buildTitle(findings.get(id));

test("a vulnerability with no open issue creates one titled with severity and id", () => {
  const [a] = plan(openCve, [], CTX);
  assert.equal(a.kind, "create");
  assert.equal(a.title, "[HIGH] CVE-2026-1111 in openssl (container image)");
  assert.deepEqual(a.labels, ["dependencies", "docker"]);
});

test("the issue the scanner would write is found again, so the second run updates it", () => {
  const existing = issue(7, titleFor(openCve, "CVE-2026-1111"));
  const actions = plan(openCve, [existing], CTX);
  assert.deepEqual(actions.map((a) => [a.kind, a.number]), [["comment", 7]]);
  assert.match(actions[0].body, /linux\/amd64, linux\/arm64/);
});

test("an issue already noting this commit is skipped, not commented again", () => {
  const first = plan(openCve, [issue(7, titleFor(openCve, "CVE-2026-1111"))], CTX)[0];
  const actions = plan(openCve, [issue(7, titleFor(openCve, "CVE-2026-1111"), LABELS, [first.body])], CTX);
  assert.deepEqual(actions.map((a) => a.kind), ["skip"]);
});

test("an id that only appears in another issue's body or in another title shape is not matched", () => {
  const human = issue(1006, "ci: move image vulnerability scanning out of CI", []);
  const longer = issue(8, "[HIGH] CVE-2026-11112 in openssl (container image)");
  assert.deepEqual(plan(openCve, [human, longer], CTX).map((a) => a.kind), ["create", "close"]);
});

test("a cleared id closes its scanner issue and names both architectures' digests", () => {
  const actions = plan(clean, [issue(7, "[HIGH] CVE-2026-1111 in openssl (container image)")], CTX);
  assert.deepEqual(actions.map((a) => [a.kind, a.number]), [["close", 7]]);
  assert.match(actions[0].body, new RegExp(`linux/amd64: \`${DIGEST.amd64}\``));
  assert.match(actions[0].body, new RegExp(`linux/arm64: \`${DIGEST.arm64}\``));
  assert.match(actions[0].body, /no longer reported as a fixable HIGH or CRITICAL/);
});

test("an issue in the scanner's title shape and labels, filed by a human, is never closed", () => {
  const human = issue(11, "[HIGH] CVE-2026-1111 in openssl (container image)", LABELS, [], "mforce");
  assert.deepEqual(plan(clean, [human], CTX), []);
  assert.deepEqual(plan(openCve, [human], CTX).map((a) => [a.kind, a.number]), [["comment", 11]]);
  assert.equal(plan(clean, [issue(12, human.title, LABELS, [], "app/github-actions")], CTX)[0].kind, "close");
});

test("database text cannot break the table, mention a user or add a link", () => {
  const nasty = vuln("CVE-2026-3", String.raw`pkg\|x`);
  nasty.Title = "ping @octocat\nsecond line";
  nasty.PrimaryURL = "javascript:alert(1)";
  const [a] = plan(collect(parsed({ amd64: [nasty], arm64: [] })), [], CTX);
  assert.ok(a.body.includes(String.raw`| pkg\\\|x |`), "the backslash is escaped before the pipe");
  assert.ok(!a.body.includes("@octocat"));
  assert.ok(a.body.includes("ping @\u200boctocat second line"));
  assert.ok(!a.body.includes("javascript:"));
});

test("a human issue is never closed, even in the scanner's title shape without its labels", () => {
  const unlabelled = issue(9, "[HIGH] CVE-2026-1111 in openssl (container image)", ["bug"]);
  assert.deepEqual(plan(clean, [unlabelled, issue(10, "CVE-2026-1111 is scary")], CTX), []);
});

test("severity and packages come from the highest severity and every affected package", () => {
  const f = collect(parsed({ amd64: [vuln("CVE-2026-2", "libssl3", "HIGH"), vuln("CVE-2026-2", "openssl", "CRITICAL")], arm64: [] }));
  assert.equal(buildTitle(f.get("CVE-2026-2")), "[CRITICAL] CVE-2026-2 in libssl3, openssl (container image)");
  assert.equal(scannerIssueId(buildTitle(f.get("CVE-2026-2"))), "CVE-2026-2");
});

test("a report that is not a completed scan of the named image is rejected", () => {
  const bad = (text, arch = "amd64") => () => parseReport(text, { repo: REPO, arch });
  assert.throws(bad("{not json"), /not JSON/);
  assert.throws(bad(report("amd64", [], { ArtifactName: `${REPO}:sha-${SHA}` })), /not a digest of/);
  assert.throws(bad(report("amd64", [], { ArtifactName: `other/repo@${DIGEST.amd64}` })), /not a digest of/);
  assert.throws(bad(report("arm64", [])), /arm64 image|scanned a arm64/);
  assert.throws(bad(report("amd64", [], { Metadata: { ImageConfig: { architecture: "amd64" } } })), /no operating system/);
  assert.throws(bad("{}"), /not a Trivy container-image report/);
});

function cli(files, openIssues) {
  const dir = mkdtempSync(join(tmpdir(), "image-scan-"));
  const args = ["--repo", "o/r", "--sha", SHA, "--image", IMAGE, "--run-url", "u", "--dry-run"];
  for (const [arch, text] of Object.entries(files)) {
    writeFileSync(join(dir, arch), text);
    args.push("--report", `${arch}=${join(dir, arch)}`);
  }
  writeFileSync(join(dir, "issues.json"), JSON.stringify(openIssues));
  args.push("--open-issues", join(dir, "issues.json"));
  return spawnSync(process.execPath, [CLI, ...args], { encoding: "utf8" });
}

test("end to end: a failed or partial scan closes nothing and exits 1", () => {
  const open = [issue(7, "[HIGH] CVE-2026-1111 in openssl (container image)")];
  const ok = cli({ amd64: report("amd64", []), arm64: report("arm64", []) }, open);
  assert.equal(ok.status, 0);
  assert.match(ok.stdout, /would close #7/);

  const broken = cli({ amd64: report("amd64", []), arm64: "Error: pull access denied" }, open);
  assert.equal(broken.status, 1);
  assert.doesNotMatch(broken.stdout, /close/);

  const missing = cli({ amd64: report("amd64", []) }, open);
  assert.equal(missing.status, 1);
  assert.match(missing.stderr, /no arm64 report/);
});
