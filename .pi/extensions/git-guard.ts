// Pi extension: runs tools/agent-guard/git_guard.py before every bash tool call and
// blocks the git writes and PR merges it refuses (#1171). powershell (Windows only)
// is refused outright, because the guard cannot check PowerShell; use bash. Pi loads
// this from .pi/extensions/ once the project is trusted; a failing handler blocks.
import { spawnSync } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

const guard = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "tools", "agent-guard", "git_guard.py");

export default function (pi: ExtensionAPI) {
  pi.on("tool_call", async (event, ctx) => {
    if (event.toolName !== "bash" && event.toolName !== "powershell") return undefined;
    const payload = JSON.stringify({ tool_name: event.toolName, tool_input: event.input, cwd: ctx.cwd });
    const result = spawnSync("python3", [guard], { input: payload, encoding: "utf8" });
    if (result.status === 0) return undefined;
    return { block: true, reason: result.stderr?.trim() || `git-guard did not run (${result.error ?? result.status})` };
  });
}
