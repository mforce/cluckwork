"""Hermes plugin: runs tools/agent-guard/git_guard.py before every terminal command,
execute_code script and process_manage input, and blocks the git writes and PR
merges it refuses (#1171).

Enable once: add `git-guard` to `plugins.enabled` in your Hermes config.yaml
(`hermes plugins enable` cannot see project plugins in Hermes 0.21.5), then start
Hermes from the repository root with HERMES_ENABLE_PROJECT_PLUGINS=true.
"""
import json
import subprocess
import sys
from pathlib import Path

GUARD = Path(__file__).resolve().parents[3] / "tools" / "agent-guard" / "git_guard.py"
TOOLS = {"terminal", "execute_code", "process_manage"}


def _terminal_place(task_id):
    """(backend, cwd) by Hermes's own precedence: task override, session record, then terminal.cwd /
    TERMINAL_CWD. ("unknown", "/") when Hermes's terminal module cannot answer, so git writes refuse."""
    try:
        from tools.terminal_tool import _get_env_config, get_session_cwd, resolve_task_overrides
        config = _get_env_config()
        cwd = resolve_task_overrides(task_id).get("cwd") or get_session_cwd(task_id) or config["cwd"]
        return config["env_type"], cwd
    except Exception:
        return "unknown", "/"


def _on_pre_tool_call(tool_name="", args=None, task_id="", **_):
    if tool_name not in TOOLS:
        return None
    backend, cwd = _terminal_place(task_id)
    payload = {"hook_event_name": "pre_tool_call", "tool_name": tool_name, "tool_input": args,
               "cwd": cwd, "backend": backend}
    result = subprocess.run([sys.executable, str(GUARD)], input=json.dumps(payload), capture_output=True, text=True)
    if result.returncode == 0:
        return None
    return {"action": "block", "message": result.stderr.strip() or f"git-guard exited {result.returncode}"}


def register(ctx):
    ctx.register_hook("pre_tool_call", _on_pre_tool_call)
