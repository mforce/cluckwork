"""Hermes plugin: runs tools/agent-guard/git_guard.py before every terminal command
and process_manage input, and blocks the git writes and PR merges it refuses
(#1171). execute_code is refused outright while this plugin is loaded: the guard
cannot check Python, so shell and git commands go through terminal.

Enable once: add `git-guard` to `plugins.enabled` in your Hermes config.yaml
(`hermes plugins enable` cannot see project plugins in Hermes 0.21.5), then start
Hermes from the repository root with HERMES_ENABLE_PROJECT_PLUGINS=true.
"""
import json
import os
import subprocess
import sys
from pathlib import Path

GUARD = Path(__file__).resolve().parents[3] / "tools" / "agent-guard" / "git_guard.py"
TOOLS = {"terminal", "process_manage", "execute_code"}


def _terminal_place(args, task_id):
    """(cwd, unchecked) for a terminal call, following terminal_tool's final resolution on the local
    backend: an absolute workdir, else the execution session's record, else the planned default (task
    override, task record, terminal.cwd / TERMINAL_CWD). unchecked names why no repository can be named."""
    try:
        from tools.approval import get_current_session_key
        from tools.terminal_tool import _get_env_config, get_session_cwd, resolve_task_overrides
        config = _get_env_config()
        if config["env_type"] != "local":
            return "/", f"it runs on the {config['env_type']} backend, where git-guard cannot read the repository"
        workdir = (args or {}).get("workdir")
        if workdir:
            if os.path.isabs(workdir):
                return workdir, None
            return "/", "its relative `workdir` resolves against a directory git-guard cannot see"
        # The terminal worker keys records by the session contextvar or task_id; when they disagree,
        # which one it uses depends on the thread it runs on.
        records = {get_session_cwd(get_current_session_key(default="") or task_id), get_session_cwd(task_id)}
        if len(records) > 1:
            return "/", "its session's recorded directory is ambiguous"
        default = resolve_task_overrides(task_id).get("cwd") or get_session_cwd(task_id) or config["cwd"]
        return records.pop() or default, None
    except Exception:
        return "/", "git-guard could not read Hermes's terminal settings"


def _on_pre_tool_call(tool_name="", args=None, task_id="", **_):
    if tool_name not in TOOLS:
        return None
    payload = {"hook_event_name": "pre_tool_call", "tool_name": tool_name, "tool_input": args, "cwd": "/"}
    if tool_name == "terminal":
        payload["cwd"], payload["unchecked"] = _terminal_place(args, task_id)
    result = subprocess.run([sys.executable, str(GUARD)], input=json.dumps(payload), capture_output=True, text=True)
    if result.returncode == 0:
        return None
    return {"action": "block", "message": result.stderr.strip() or f"git-guard exited {result.returncode}"}


def register(ctx):
    ctx.register_hook("pre_tool_call", _on_pre_tool_call)
