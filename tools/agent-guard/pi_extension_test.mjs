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

  const merge = await toolCall("bash", "gh pr merge 1158 --squash", repo);
  assert.equal(merge.block, true);
  assert.match(merge.reason, /git-guard: .*only the maintainer merges/);

  const commitOnMain = await toolCall("bash", "git commit -m x", repo);
  assert.equal(commitOnMain.block, true);
  assert.match(commitOnMain.reason, /main is protected/);

  assert.equal(await toolCall("bash", "git status --short", repo), undefined);
  assert.equal(await toolCall("read", "gh pr merge 1158", repo), undefined);
});
