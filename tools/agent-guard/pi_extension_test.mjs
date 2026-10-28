// Drives .pi/extensions/git-guard.ts the way Pi does: register the extension, then call its
// tool_call handler with Pi's bash event and context. Run: node --test tools/agent-guard/pi_extension_test.mjs
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

const { default: extension } = await import("../../.pi/extensions/git-guard.ts");
const handlers = {};
extension({ on: (name, handler) => (handlers[name] = handler) });
const toolCall = (toolName, command, cwd) =>
  handlers.tool_call({ type: "tool_call", toolCallId: "c1", toolName, input: { command } }, { cwd });

test("blocks what git_guard.py refuses and lets the rest run", async (t) => {
  const repo = mkdtempSync(join(tmpdir(), "pi-guard-"));
  t.after(() => rmSync(repo, { recursive: true, force: true }));
  execFileSync("git", ["init", "-q", "-b", "main", repo]);

  const force = await toolCall("bash", "git push --force", repo);
  assert.equal(force.block, true);
  assert.match(force.reason, /git-guard: .*never a rewrite of pushed history/);

  const commitOnMain = await toolCall("bash", "git commit -m x", repo);
  assert.equal(commitOnMain.block, true);
  assert.match(commitOnMain.reason, /main is protected/);

  assert.equal(await toolCall("bash", "git status --short", repo), undefined);

  const powershell = await toolCall("powershell", "Get-ChildItem", repo);
  assert.equal(powershell.block, true);
  assert.match(powershell.reason, /powershell tool is blocked in this repository: use the bash tool/);
  assert.equal(await toolCall("read", "git push --force", repo), undefined);
});
