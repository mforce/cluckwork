"""Hermes plugin: runs tools/agent-guard/git_guard.py before every terminal command
and blocks the git writes and PR merges it refuses (#1171).

Enable once: start Hermes with HERMES_ENABLE_PROJECT_PLUGINS=true and run
`hermes plugins enable git-guard`.
"""
import json
import os
import subprocess
import sys
from pathlib import Path

GUARD = Path(__file__).resolve().parents[3] / "tools" / "agent-guard" / "git_guard.py"


def _session_cwd(task_id):
    """The terminal's directory as Hermes records it; None outside Hermes."""
    try:
        from tools.terminal_tool import get_session_cwd
    except ImportError:
        return None
    return get_session_cwd(task_id)


def _on_pre_tool_call(tool_name="", args=None, task_id="", **_):
    if tool_name != "terminal":
        return None
    payload = {"hook_event_name": "pre_tool_call", "tool_name": tool_name, "tool_input": args,
               "cwd": _session_cwd(task_id) or os.getcwd()}
    result = subprocess.run([sys.executable, str(GUARD)], input=json.dumps(payload), capture_output=True, text=True)
    if result.returncode == 0:
        return None
    return {"action": "block", "message": result.stderr.strip() or f"git-guard exited {result.returncode}"}


def register(ctx):
    ctx.register_hook("pre_tool_call", _on_pre_tool_call)
