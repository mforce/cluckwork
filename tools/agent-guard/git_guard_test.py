#!/usr/bin/env python3
"""Table test for git_guard.py, fed through stdin in both harness payload shapes.

Run: python3 tools/agent-guard/git_guard_test.py
"""
import json
import os
import subprocess
import sys
import tempfile
import unittest

GUARD = os.path.join(os.path.dirname(os.path.abspath(__file__)), "git_guard.py")

# (command, directory the session runs in, expected verdict).
# "main" and "feature" are repos on those branches; "feature" sits inside "main".
# "tracking" is on feat/t with push.default=upstream and origin/main as upstream.
CASES = [
    ("git push", "feature", "allow"),
    ("git push -u origin feat/x", "feature", "allow"),
    ("git push -u origin HEAD", "feature", "allow"),
    ("git push --force-with-lease", "feature", "allow"),
    ("git commit -m 'wip'", "feature", "allow"),
    ("git status && git log --oneline -3", "main", "allow"),
    ("git fetch origin main:main", "main", "allow"),
    ("gh pr create -R mforce/cluckwork --title x", "feature", "allow"),
    ("gh pr view 12 --json state", "main", "allow"),
    ("echo 'git push --force origin HEAD:main'", "main", "allow"),
    ("grep -rn 'gh pr merge' docs", "main", "allow"),
    ("ls -la | wc -l", "main", "allow"),
    ("git commit -F - <<'EOF'\ngit push --force origin HEAD:main\nEOF", "feature", "allow"),
    ("cd feature && git commit -m x", "main", "allow"),
    ("git -C feature push", "main", "allow"),
    ("git push 2>&1 | tail -5", "feature", "allow"),
    ("gh pr merge 12 --squash", "feature", "block"),
    ("gh -R mforce/cluckwork pr merge 12", "feature", "block"),
    ("gh api -X PUT repos/mforce/cluckwork/pulls/12/merge", "feature", "block"),
    ("git push origin HEAD:main", "feature", "block"),
    ("git push origin feat/x:feat/y", "feature", "block"),
    ("git push origin :old-branch", "feature", "block"),
    ("git push --force", "feature", "block"),
    ("git push -f origin feat/x", "feature", "block"),
    ("git push -uf origin feat/x", "feature", "block"),
    ("git push origin +feat/x", "feature", "block"),
    ("git push --mirror", "feature", "block"),
    ("git push --all origin", "feature", "block"),
    ("git push origin main", "feature", "block"),
    ("git push origin refs/heads/main", "feature", "block"),
    ("git push", "main", "block"),
    ("git push -u origin HEAD", "main", "block"),
    ("git push", "tracking", "block"),
    ("git push origin 2>/dev/null", "main", "block"),
    ("git commit -m x", "main", "block"),
    ("git add -A && git commit -m x", "main", "block"),
    ("cd .. && git commit -m x", "feature", "block"),
    ("git -C .. commit -m x", "feature", "block"),
    ("GIT_TRACE=1 git push --force", "feature", "block"),
    ("env FOO=1 nohup git push -f", "feature", "block"),
    ("timeout 60 git push origin HEAD:main", "feature", "block"),
    ("bash -lc 'git push --force'", "feature", "block"),
    ("sg docker -c 'git commit -m x'", "main", "block"),
    ("cd \"$SOMEWHERE\" && git commit -m x", "feature", "block"),
    ("cd new-dir && git push", "feature", "block"),
    ("git commit -m \"unclosed", "feature", "block"),
]


def payload(harness, command, cwd):
    if harness == "claude":  # captured from Claude Code 2.1.296
        return {"session_id": "s", "transcript_path": "/t.jsonl", "cwd": cwd, "permission_mode": "default",
                "hook_event_name": "PreToolUse", "tool_name": "Bash",
                "tool_input": {"command": command, "description": "d"}, "tool_use_id": "toolu_1"}
    # Codex: codex-rs hook_runtime.rs PreToolUseRequest; exec_command sends only {"command"}
    return {"session_id": "s", "turn_id": "t", "cwd": cwd, "transcript_path": None, "model": "m",
            "permission_mode": "default", "hook_event_name": "PreToolUse", "tool_name": "Bash",
            "tool_use_id": "call_1", "tool_input": {"command": command}}


def run_guard(harness, command, cwd):
    result = subprocess.run([sys.executable, GUARD], input=json.dumps(payload(harness, command, cwd)),
                            capture_output=True, text=True)
    return result.returncode, result.stderr.strip()


class GitGuardTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.dirs = {"main": os.path.join(cls.tmp.name, "repo")}
        cls.dirs["feature"] = os.path.join(cls.dirs["main"], "feature")
        os.makedirs(cls.dirs["feature"])
        subprocess.run(["git", "init", "-q", "-b", "main", cls.dirs["main"]], check=True)
        subprocess.run(["git", "init", "-q", "-b", "feat/x", cls.dirs["feature"]], check=True)
        remote = os.path.join(cls.tmp.name, "remote.git")
        tracking = cls.dirs["tracking"] = os.path.join(cls.tmp.name, "tracking")
        for args in (["init", "-q", "--bare", "-b", "main", remote],
                     ["init", "-q", "-b", "main", tracking],
                     ["-C", tracking, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--no-verify",
                      "--allow-empty", "-m", "init"],
                     ["-C", tracking, "remote", "add", "origin", remote],
                     ["-C", tracking, "push", "-q", "origin", "main"],
                     ["-C", tracking, "switch", "-q", "-c", "feat/t", "--track", "origin/main"],
                     ["-C", tracking, "config", "push.default", "upstream"]):
            subprocess.run(["git", *args], check=True)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_table(self):
        for harness in ("claude", "codex"):
            for command, where, expected in CASES:
                code, stderr = run_guard(harness, command, self.dirs[where])
                verdict = {0: "allow", 2: "block"}.get(code, f"exit {code}")
                print(f"{harness:6} {verdict:5} [{where:8}] {command!r}" + (f"\n{'':23}{stderr}" if stderr else ""))
                with self.subTest(harness=harness, command=command):
                    self.assertEqual(verdict, expected, stderr)
                    if expected == "block":
                        self.assertIn("git-guard:", stderr)

    def test_ignores_payloads_without_a_command(self):
        for body in ("", "not json", json.dumps({"tool_name": "Read", "tool_input": {"file_path": "/x"}})):
            result = subprocess.run([sys.executable, GUARD], input=body, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, body)


if __name__ == "__main__":
    unittest.main()
