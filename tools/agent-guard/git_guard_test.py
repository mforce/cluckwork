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

# (command, directory the session runs in, expected verdict). Fixtures:
#   main      repo on main
#   feature   repo on feat/x inside main; remotes `backup` (push = HEAD:refs/heads/main), `mirror`
#             (mirror = true) and `paseo` (push = HEAD:refs/heads/feat/y, as Paseo PR checkouts have)
#   tracking  repo on feat/t, upstream origin/main, push.default=upstream
#   matching  repo on feat/m with push.default=matching
#   plain     a directory outside any repo
CASES = [
    # Plain forms agents need.
    ("git push", "feature", "allow"),
    ("git push -u origin feat/x", "feature", "allow"),
    ("git push -u origin HEAD", "feature", "allow"),
    ("git push --force-with-lease", "feature", "allow"),
    ("git push --force-w origin feat/x", "feature", "allow"),
    ("git push -o ci.skip origin feat/x", "feature", "allow"),
    ("git push paseo", "feature", "allow"),
    ("git push 2>&1", "feature", "allow"),
    ("git commit -m 'wip'", "feature", "allow"),
    ("git commit -m \"subject\n\nbody line\"", "feature", "allow"),
    ("cd feature && git commit -m x", "main", "allow"),
    ("git -C feature push", "main", "allow"),
    ("git status && git log --oneline -3", "main", "allow"),
    ("git fetch origin main:main", "main", "allow"),
    ("git log --grep 'push' --oneline", "main", "allow"),
    ("gh pr create -R mforce/cluckwork --title x", "feature", "allow"),
    ("gh pr view 12 --json state", "main", "allow"),
    ("gh api repos/mforce/cluckwork/pulls/1158/merge", "feature", "allow"),
    ("gh api -X GET repos/mforce/cluckwork/pulls/1158/merge", "feature", "allow"),
    ("gh api -XGET repos/mforce/cluckwork/pulls/1158/merge", "feature", "allow"),
    ("echo 'git push --force origin HEAD:main'", "main", "allow"),
    ("echo ';' git push --force", "main", "allow"),
    ("grep -rn 'gh pr merge' docs", "main", "allow"),
    ("ls -la | wc -l", "main", "allow"),
    # Merges.
    ("gh pr merge 12 --squash", "feature", "block"),
    ("gh -R mforce/cluckwork pr merge 12", "feature", "block"),
    ("gh api -X PUT repos/mforce/cluckwork/pulls/12/merge", "feature", "block"),
    ("gh api --method=PUT repos/mforce/cluckwork/pulls/12/merge", "feature", "block"),
    ("gh api -XPUT repos/mforce/cluckwork/pulls/12/merge", "feature", "block"),
    ("gh api repos/mforce/cluckwork/pulls/12/merge -f merge_method=squash", "feature", "block"),
    ("gh api graphql -f query='mutation { mergePullRequest(input: {pullRequestId: \"PR_x\"}) { clientMutationId } }'",
     "feature", "block"),
    ("gh alias set land 'pr merge'", "feature", "block"),
    ("gh alias set land \"pr merge\" && gh land 1158", "feature", "block"),
    ("gh alias import aliases.yml", "feature", "block"),
    ("gh api graphql -f query='mutation { enablePullRequestAutoMerge(input: {}) { clientMutationId } }'",
     "feature", "block"),
    # Refspecs, force, main.
    ("git push origin HEAD:main", "feature", "block"),
    ("git push origin feat/x:feat/y", "feature", "block"),
    ("git push origin :old-branch", "feature", "block"),
    ("git push --force", "feature", "block"),
    ("git push -f origin feat/x", "feature", "block"),
    ("git push -uf origin feat/x", "feature", "block"),
    ("git push origin +feat/x", "feature", "block"),
    ("git push --mirror", "feature", "block"),
    ("git push --all origin", "feature", "block"),
    ("git push --mir origin", "feature", "block"),
    ("git push --al origin", "feature", "block"),
    ("git push --pru origin", "feature", "block"),
    ("git push --forc origin", "feature", "block"),
    ("git push -ofoo origin HEAD:main", "feature", "block"),
    ("git push origin main", "feature", "block"),
    ("git push origin refs/heads/main", "feature", "block"),
    ("git push --delete origin main", "feature", "block"),
    ("git push origin @", "main", "block"),
    ("git push", "main", "block"),
    ("git push -u origin HEAD", "main", "block"),
    ("git push origin 2>/dev/null", "main", "block"),
    ("git commit -m x", "main", "block"),
    ("cd .. && git commit -m x", "feature", "block"),
    ("git -C .. commit -m x", "feature", "block"),
    ("cd new-dir && git push", "feature", "block"),
    ("cd - && git commit -m x", "feature", "block"),
    ("git commit -m x", "plain", "block"),
    # Push configuration (finding 3).
    ("git push", "tracking", "block"),
    ("git push -u origin feat/t", "tracking", "block"),
    ("git push origin feat/t", "tracking", "block"),
    ("git push --force-with-lease origin feat/t", "tracking", "block"),
    ("git -c remote.origin.push=refs/heads/feat/x:refs/heads/main push -u origin feat/x", "feature", "block"),
    ("git -c remote.origin.push=+refs/heads/feat/x:refs/heads/feat/x push -u origin feat/x", "feature", "block"),
    ("git -c push.default=matching push", "feature", "block"),
    ("git -c remote.origin.mirror=true push", "feature", "block"),
    ("git -c alias.p=push p", "feature", "block"),
    ("git --config-env=push.default=MODE push", "feature", "block"),
    ("git -c remote.origin.pushurl=../other.git push", "feature", "block"),
    ("git push backup", "feature", "block"),
    ("git push mirror", "feature", "block"),
    ("git push", "matching", "block"),
    # Shapes the guard refuses instead of parsing (findings 1, 2, 4).
    ("git status &&\ngit push origin HEAD:main", "feature", "block"),
    ("git status;\ngh pr merge 1158", "feature", "block"),
    ("git status\n\ngh pr merge 1158", "feature", "block"),
    ("git status # inspect first\ngit push --force", "feature", "block"),
    ("if true; then git push origin HEAD:main; fi", "feature", "block"),
    ("for branch in main; do git push origin main; done", "feature", "block"),
    ("{ git push --force; }", "feature", "block"),
    ("bash <<'EOF'\ngh pr merge 1158\nEOF", "feature", "block"),
    ("echo \"$(git push --force)\"", "feature", "block"),
    ("echo `git push --force`", "feature", "block"),
    ("git commit -F - <<'EOF'\nsubject\nEOF", "feature", "block"),
    ("git switch main && git commit -m x", "tracking", "block"),
    ("git checkout main && git push", "tracking", "block"),
    ("git switch -c feat/new && git commit -m x", "main", "block"),
    ("(cd feature && git status); git commit -m x", "main", "block"),
    ("cd feature | cat; git commit -m x", "main", "block"),
    ("false && cd feature; git commit -m x", "main", "block"),
    ("git add -A && git commit -m x", "feature", "block"),
    ("git push 2>&1 | tail -5", "feature", "block"),
    ("/usr/bin/env git push --force", "feature", "block"),
    ("command -- gh pr merge 1158", "feature", "block"),
    ("command -p git push --force", "feature", "block"),
    ("timeout --signal TERM 60 git push --force", "feature", "block"),
    ("env -C .. git commit -m x", "feature", "block"),
    ("GIT_DIR=../.git git commit -m x", "feature", "block"),
    ("bash -lc 'git push origin feat/x'", "feature", "block"),
    ("git push origin $BRANCH", "feature", "block"),
    ("git push origin \\\nHEAD:main", "feature", "block"),
    ("git push \\\n origin", "main", "block"),
    ("git push # note", "main", "block"),
    ("git status & git commit -m x", "main", "block"),
    ("git commit -m \"unclosed", "feature", "block"),
]

MALFORMED = ["", "not json", "{\"tool_name\": \"Bash\"", "{}", "[]", "null", "[" * 100000,
             json.dumps({"tool_name": "Bash", "tool_input": {"command": 7}}),
             json.dumps({"tool_name": "Bash", "tool_input": {"cmd": "git push --force"}}),
             json.dumps({"tool_name": "Bash", "tool_input": {"command": "git commit -m x"}, "cwd": 3})]


def payload(harness, command, cwd):
    if harness == "claude":  # captured from Claude Code 2.1.296
        return {"session_id": "s", "transcript_path": "/t.jsonl", "cwd": cwd, "permission_mode": "default",
                "hook_event_name": "PreToolUse", "tool_name": "Bash",
                "tool_input": {"command": command, "description": "d"}, "tool_use_id": "toolu_1"}
    # Codex: codex-rs hook_runtime.rs PreToolUseRequest; exec_command sends only {"command"}
    return {"session_id": "s", "turn_id": "t", "cwd": cwd, "transcript_path": None, "model": "m",
            "permission_mode": "default", "hook_event_name": "PreToolUse", "tool_name": "Bash",
            "tool_use_id": "call_1", "tool_input": {"command": command}}


def run_guard(body):
    result = subprocess.run([sys.executable, GUARD], input=body, capture_output=True, text=True)
    return result.returncode, result.stderr.strip()


def git(*args):
    subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@t", *args], check=True, capture_output=True)


class GitGuardTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        root = cls.tmp.name
        main = os.path.join(root, "repo")
        cls.dirs = {"main": main, "feature": os.path.join(main, "feature"),
                    "tracking": os.path.join(root, "tracking"), "matching": os.path.join(root, "matching"), "plain": root}
        feature, tracking, remote = cls.dirs["feature"], cls.dirs["tracking"], os.path.join(root, "remote.git")
        git("init", "-q", "-b", "main", main)
        git("init", "-q", "-b", "feat/x", feature)
        git("-C", feature, "config", "remote.backup.url", remote)
        git("-C", feature, "config", "remote.backup.push", "HEAD:refs/heads/main")
        git("-C", feature, "config", "remote.mirror.url", remote)
        git("-C", feature, "config", "remote.mirror.mirror", "true")
        git("-C", feature, "config", "remote.paseo.url", remote)
        git("-C", feature, "config", "remote.paseo.push", "HEAD:refs/heads/feat/y")
        git("init", "-q", "-b", "feat/m", cls.dirs["matching"])
        git("-C", cls.dirs["matching"], "config", "push.default", "matching")
        git("init", "-q", "--bare", "-b", "main", remote)
        git("init", "-q", "-b", "main", tracking)
        git("-C", tracking, "commit", "-q", "--no-verify", "--allow-empty", "-m", "init")
        git("-C", tracking, "remote", "add", "origin", remote)
        git("-C", tracking, "update-ref", "refs/remotes/origin/main", "HEAD")
        git("-C", tracking, "switch", "-q", "-c", "feat/t", "--track", "origin/main")
        git("-C", tracking, "config", "push.default", "upstream")

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_table(self):
        counts = {}
        for harness in ("claude", "codex"):
            for command, where, expected in CASES:
                code, stderr = run_guard(json.dumps(payload(harness, command, self.dirs[where])))
                verdict = {0: "allow", 2: "block"}.get(code, f"exit {code}")
                counts[verdict] = counts.get(verdict, 0) + 1
                print(f"{harness:6} {verdict:5} [{where:8}] {command!r}" + (f"\n{'':23}{stderr}" if stderr else ""))
                with self.subTest(harness=harness, command=command):
                    self.assertEqual(verdict, expected, stderr)
                    if expected == "block":
                        self.assertIn("git-guard:", stderr)
        print(f"{len(CASES)} commands x 2 payload shapes: {counts}")

    def test_malformed_bash_payload_blocks(self):
        for body in MALFORMED:
            code, stderr = run_guard(body)
            with self.subTest(body=body[:60]):
                self.assertEqual(code, 2, stderr)
                self.assertIn("unreadable Bash hook payload", stderr)

    def test_other_tools_pass(self):
        code, _ = run_guard(json.dumps({"tool_name": "Read", "tool_input": {"file_path": "/x"}}))
        self.assertEqual(code, 0)

    def test_switch_then_separate_commit(self):
        with tempfile.TemporaryDirectory() as repo:
            git("init", "-q", "-b", "main", repo)
            for command, expected in (("git commit -m x", 2), ("git switch -c feat/new", 0), ("git commit -m x", 0)):
                code, stderr = run_guard(json.dumps(payload("claude", command, repo)))
                self.assertEqual(code, expected, f"{command}: {stderr}")
                if command.startswith("git switch"):
                    git("-C", repo, "switch", "-q", "-c", "feat/new")


if __name__ == "__main__":
    unittest.main()
