// Self-tests for the weekly image scan's issue logic. Run with
// `node --test .github/scripts/image-scan-issues.test.mjs`.
//
// The cases that matter: an issue that exists is updated, never duplicated; a
// cleared id is closed only on a completed scan of both architectures; and a
// human issue is never closed, whatever its body says.

import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

import { LABELS, buildTitle, collect, parseReport, plan, scannerIssueId } from "./image-scan-issues.mjs";

const SHA = "a".repeat(40);
const REPO = "ghcr.io/o/r";
const IMAGE = `${REPO}:sha-${SHA}`;
const DIGEST = { amd64: `sha256:${"a".repeat(64)}`, arm64: `sha256:${"b".repeat(64)}` };
const CTX = { sha: SHA, ref: IMAGE, runUrl: "https://example.invalid/run", digests: DIGEST };
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

const issue = (number, title, labels = LABELS, comments = [], author = "github-actions[bot]", state = "open") => ({ number, title, labels, comments, author, state });
const closedIssue = (number, title, labels = LABELS) => issue(number, title, labels, [], "mforce", "closed");
const IGN = [...LABELS, "ignored"];
const TITLE_1111 = "[HIGH] CVE-2026-1111 in openssl (container image)";
const kinds = (actions) => actions.map((a) => [a.kind, a.number]);

const openCve = collect(parsed({ amd64: [vuln("CVE-2026-1111", "openssl")], arm64: [vuln("CVE-2026-1111", "openssl")] }));
const clean = collect(parsed({ amd64: [], arm64: [] }));
const titleFor = (findings, id) => buildTitle(findings.get(id));

test("a vulnerability with no open issue creates one titled with severity and id", () => {
  const [a] = plan(openCve, [], CTX);
  assert.equal(a.kind, "create");
  assert.equal(a.title, "[HIGH] CVE-2026-1111 in openssl (container image)");
  assert.ok(a.body.includes("(<https://example.invalid/CVE-2026-1111>)"), "the advisory link is kept as an autolink");
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

test("closed and present: label it ignored once, never reopen, comment or create", () => {
  const closed = closedIssue(20, TITLE_1111);
  assert.deepEqual(kinds(plan(openCve, [closed], CTX)), [["label", 20]]);
  const labelled = closedIssue(20, TITLE_1111, IGN);
  assert.deepEqual(kinds(plan(openCve, [labelled], CTX)), [["skip", 20]]);
});

test("the ignored comment fires only on the run that adds the label", () => {
  const [add] = plan(openCve, [closedIssue(20, TITLE_1111)], CTX);
  assert.equal(add.kind, "label");
  assert.match(add.body, /CVE-2026-1111 \(HIGH\) is still present as of `sha-a+` on linux\/amd64, linux\/arm64/);
  assert.match(add.body, new RegExp(`linux/amd64: \`${DIGEST.amd64}\``));
  assert.match(add.body, /Reopen this issue to stop ignoring it/);
  const second = plan(openCve, [closedIssue(20, TITLE_1111, IGN)], CTX);
  assert.deepEqual(kinds(second), [["skip", 20]]);
  assert.equal(second[0].body, undefined);
});

test("a CVE that cleared and then returns is re-labelled with a new comment", () => {
  assert.deepEqual(kinds(plan(clean, [closedIssue(22, TITLE_1111, IGN)], CTX)), [["unlabel", 22]]);
  assert.equal(plan(clean, [closedIssue(22, TITLE_1111, IGN)], CTX)[0].body, undefined);
  const back = plan(openCve, [closedIssue(22, TITLE_1111)], CTX);
  assert.deepEqual(kinds(back), [["label", 22]]);
  assert.ok(back[0].body.includes("still present"));
});

test("removing the label on reopen posts no ignored comment", () => {
  const actions = plan(openCve, [issue(21, TITLE_1111, IGN)], CTX);
  assert.deepEqual(actions.filter((a) => a.kind === "unlabel").map((a) => a.body), [undefined]);
  assert.ok(actions.every((a) => a.kind !== "label"));
});

test("a closed issue is matched by title only, so a closed human issue without the CVE in its title does not hide it", () => {
  const closed1006 = closedIssue(1006, "ci: move image vulnerability scanning out of CI to a weekly scan", []);
  assert.deepEqual(plan(openCve, [closed1006], CTX).map((a) => a.kind), ["create"]);
});

test("reopened and ignored: drop the label, then update", () => {
  const reopened = issue(21, TITLE_1111, IGN);
  assert.deepEqual(kinds(plan(openCve, [reopened], CTX)), [["unlabel", 21], ["comment", 21]]);
});

test("closed, ignored and absent: the label comes off, and only then", () => {
  assert.deepEqual(kinds(plan(clean, [closedIssue(22, TITLE_1111, IGN)], CTX)), [["unlabel", 22]]);
  assert.deepEqual(plan(clean, [closedIssue(22, TITLE_1111)], CTX), []);
});

test("an open ignored issue whose CVE cleared is closed and loses the label in one run", () => {
  assert.deepEqual(kinds(plan(clean, [issue(23, TITLE_1111, IGN)], CTX)), [["close", 23], ["unlabel", 23]]);
});

test("both an open and a closed match: the open one is updated, the closed one untouched", () => {
  const actions = plan(openCve, [closedIssue(5, TITLE_1111, IGN), issue(30, TITLE_1111)], CTX);
  assert.deepEqual(kinds(actions), [["comment", 30]]);
});

function cli(files, issues) {
  const dir = mkdtempSync(join(tmpdir(), "image-scan-"));
  const args = ["--repo", "o/r", "--sha", SHA, "--image", IMAGE, "--run-url", "u", "--dry-run"];
  for (const [arch, text] of Object.entries(files)) {
    writeFileSync(join(dir, arch), text);
    args.push("--report", `${arch}=${join(dir, arch)}`);
  }
  writeFileSync(join(dir, "issues.json"), JSON.stringify(issues));
  args.push("--issues", join(dir, "issues.json"));
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

  const ignored = [...open, closedIssue(40, "[HIGH] CVE-2026-2222 in zlib (container image)", IGN)];
  assert.match(cli({ amd64: report("amd64", []), arm64: report("arm64", []) }, ignored).stdout, /would remove the ignored label from #40/);
  const failedWithIgnored = cli({ amd64: report("amd64", []), arm64: "Error" }, ignored);
  assert.equal(failedWithIgnored.status, 1);
  assert.doesNotMatch(failedWithIgnored.stdout, /label|close/);

  const missing = cli({ amd64: report("amd64", []) }, open);
  assert.equal(missing.status, 1);
  assert.match(missing.stderr, /no arm64 report/);
});

test("live run: the label is ensured, then comment and label land once, and a second run touches nothing", () => {
  const dir = mkdtempSync(join(tmpdir(), "image-scan-live-"));
  const log = join(dir, "gh.log");
  mkdirSync(join(dir, "bin"));
  writeFileSync(join(dir, "bin", "gh"), `#!/bin/sh\necho "$*" >> "${log}"\ncat > /dev/null\n`, { mode: 0o755 });
  const run = (issues) => {
    writeFileSync(join(dir, "issues.json"), JSON.stringify(issues));
    const args = ["--repo", "o/r", "--sha", SHA, "--image", IMAGE, "--run-url", "u", "--issues", join(dir, "issues.json")];
    for (const arch of ["amd64", "arm64"]) {
      writeFileSync(join(dir, arch), report(arch, [vuln("CVE-2026-1111", "openssl")]));
      args.push("--report", `${arch}=${join(dir, arch)}`);
    }
    const r = spawnSync(process.execPath, [CLI, ...args], { encoding: "utf8", env: { ...process.env, PATH: `${join(dir, "bin")}:${process.env.PATH}` } });
    assert.equal(r.status, 0, r.stderr);
    return existsSync(log) ? readFileSync(log, "utf8").trim().split("\n").map((l) => l.split(" ").slice(0, 3).join(" ")) : [];
  };
  assert.deepEqual(run([closedIssue(20, TITLE_1111)]), ["label create ignored", "issue comment 20", "issue edit 20"]);
  rmSync(log);
  assert.deepEqual(run([closedIssue(20, TITLE_1111, IGN)]), []);
});
