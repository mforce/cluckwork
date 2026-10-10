#!/usr/bin/env python3
"""Table test for git_guard.py, fed through stdin in each harness's payload shape.

Run: python3 tools/agent-guard/git_guard_test.py
"""
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import types
import unittest

GUARD = os.path.join(os.path.dirname(os.path.abspath(__file__)), "git_guard.py")
ROOT = os.getcwd()
HERMES_PLUGIN = os.path.join(os.path.dirname(GUARD), "..", "..", ".hermes", "plugins", "git-guard", "__init__.py")

# (command, directory the session runs in, expected verdict). Fixtures:
#   main      repo on main
#   feature   repo on feat/x inside main, with `link` -> main/sub and directories `--` and `~`; remotes `backup` (push = HEAD:refs/heads/main), `mirror`
#             (mirror = true) and `paseo` (push = HEAD:refs/heads/feat/y, as Paseo PR checkouts have)
#   tracking  repo on feat/t, upstream origin/main, push.default=upstream
#   matching  repo on feat/m with push.default=matching
#   plain     a directory outside any repo
#   solo      repo on feat/x whose only remote, `backup`, has push = HEAD:refs/heads/main
#   tagged    repo on main that also has a tag named main
#   feature also has remotes `matchall` (push = :), `mirror2` (mirror = 2), `nomirror` (mirror = false)
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
    ("git push origin feat/x 2>/dev/null", "feature", "allow"),
    ("git commit -m 'a > b && c'", "feature", "allow"),
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
    ("gh api 'repos/mforce/cluckwork/pulls/1158/merge?x=1'", "feature", "allow"),
    ("gh api https://api.github.com/graphql -f query='query { viewer { login } }'", "feature", "allow"),
    ("echo 'git push --force origin HEAD:main'", "main", "allow"),
    ("echo ';' git push --force", "main", "allow"),
    ("grep -rn 'gh pr merge' docs", "main", "allow"),
    ("ls -la | wc -l", "main", "allow"),
    ("ls *.py | wc -l", "main", "allow"),
    ("git rev-parse @{upstream}", "tracking", "allow"),
    ("git log @{u}..HEAD --oneline", "tracking", "allow"),
    ("git show HEAD@{1}", "tracking", "allow"),
    ("gh api repos/{owner}/{repo}/pulls/1158", "feature", "allow"),
    ("git log --format=\"%h %s (%cr)\" | head -20", "tracking", "allow"),
    ("gh pr view 1158 --json number,title --jq '{number, title}' | cat", "feature", "allow"),
    ("gh api graphql -f query='query { repository(owner:\"mforce\", name:\"cluckwork\") { name } }' | cat", "feature", "allow"),
    ("gh api 'repos/mforce/cluckwork/pulls?state=open' --jq '.[].number'", "main", "allow"),
    # Merges.
    ("gh pr merge 12 --squash", "feature", "block"),
    ("gh -R mforce/cluckwork pr merge 12", "feature", "block"),
    ("gh api -X PUT repos/mforce/cluckwork/pulls/12/merge", "feature", "block"),
    ("gh api --method=PUT repos/mforce/cluckwork/pulls/12/merge", "feature", "block"),
    ("gh api -XPUT repos/mforce/cluckwork/pulls/12/merge", "feature", "block"),
    ("gh api repos/mforce/cluckwork/pulls/12/merge -f merge_method=squash", "feature", "block"),
    ("gh api graphql -f query='mutation { mergePullRequest(input: {pullRequestId: \"PR_x\"}) { clientMutationId } }'",
     "feature", "block"),
    ("gh api https://api.github.com/graphql -f query='mutation { mergePullRequest(input: {pullRequestId: \"PR_x\"}) "
     "{ clientMutationId } }'", "feature", "block"),
    ("gh api /graphql -f query='mutation { mergePullRequest(input: {}) { clientMutationId } }'", "feature", "block"),
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
    ("cd -- && git commit -m x", "feature", "block"),
    ("cd -P feature && git commit -m x", "main", "block"),
    ("cd \"~\" && git commit -m x", "feature", "block"),
    ("cd ~ && git commit -m x", "feature", "block"),
    ("git -C link/.. commit -m x", "feature", "block"),
    ("git -C missing-dir commit -m x", "feature", "block"),
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
    ("git push", "solo", "block"),
    ("git push matchall", "feature", "block"),
    ("git push mirror2", "feature", "block"),
    ("git push nomirror", "feature", "allow"),
    ("git commit -m x", "tagged", "block"),
    ("git push", "tagged", "block"),
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
    # Quoting that the shell joins back into a write (parser differentials).
    ("git pu\"\"sh origin HEAD:main", "feature", "block"),
    ("g'i't push --force", "feature", "block"),
    ("git p\\ush --force", "feature", "block"),
    ("git $'\\x70ush' --force", "feature", "block"),
    ("gh pr mer''ge 12", "feature", "block"),
    ("git push origin ma?n", "feature", "block"),
    ("gh api -X PUT 'repos/mforce/cluckwork/pulls/12/merge?x=1'", "feature", "block"),
    ("gh api -X PUT 'repos/mforce/cluckwork/pulls/12/merge#x'", "feature", "block"),
    ("gh api -X PUT repos/mforce/cluckwork/pulls/12/merg%65", "feature", "block"),
    ("gh api -X PUT https://api.github.com/repos/mforce/cluckwork/pulls/12/merge/", "feature", "block"),
    ("gh api -X PATCH repos/mforce/cluckwork/pulls/12 -f state=open --jq .mergeable", "feature", "allow"),
    ("rg --pre ./run.sh 'git push' .", "feature", "block"),
    ("/usr/bin/gi? push --force", "feature", "block"),
    ("git pu\\\nsh --force", "feature", "block"),
    ("g\\\nit push --force", "feature", "block"),
    ("gh pr mer\\\nge 1158", "feature", "block"),
    ("git com\\\nmit -m x", "feature", "block"),
    ("git push 2 > /tmp/push.log main", "feature", "block"),
    ("git push origin 1158 > /tmp/push.log", "feature", "block"),
    ("git push origin feat/x >> /tmp/push.log", "feature", "block"),
    ("git push origin feat/x &> /tmp/push.log", "feature", "block"),
    ("$G push --force", "feature", "block"),
    ("git $VERB --force", "feature", "block"),
    ("git -C \"$WT\" status", "feature", "block"),
]

# Known limits, documented in the PR body as contrived: brace and extglob expansion can spell a verb.
CASES += [
    ("git pu{s..s}h origin HEAD:main", "feature", "allow"),
    ("git p@(u)sh --force", "feature", "allow"),
]

MALFORMED = ["", "not json", "{\"tool_name\": \"Bash\"", "{}", "[]", "null", "[" * 100000,
             json.dumps({"tool_name": "Bash", "tool_input": {"command": 7}}),
             json.dumps({"tool_name": "Bash", "tool_input": {"cmd": "git push --force"}}),
             json.dumps({"tool_name": "Bash", "tool_input": {"command": "git commit -m x"}, "cwd": 3})]
MALFORMED += [json.dumps({"tool_name": "Bash", "tool_input": {"command": "git commit -m x"}, **cwd})
              for cwd in ({}, {"cwd": None}, {"cwd": ""}, {"cwd": 0}, {"cwd": False}, {"cwd": []}, {"cwd": {}})]
MALFORMED += [json.dumps({"tool_name": name, "tool_input": {"command": "git commit -m x"}, "cwd": "/"})
              for name in (None, 0, False, [], {}, "")]


def payload(harness, command, cwd):
    if harness == "claude":  # captured from Claude Code 2.1.296
        return {"session_id": "s", "transcript_path": "/t.jsonl", "cwd": cwd, "permission_mode": "default",
                "hook_event_name": "PreToolUse", "tool_name": "Bash",
                "tool_input": {"command": command, "description": "d"}, "tool_use_id": "toolu_1"}
    if harness == "codex":  # codex-rs hook_runtime.rs PreToolUseRequest; exec_command sends only {"command"}
        return {"session_id": "s", "turn_id": "t", "cwd": cwd, "transcript_path": None, "model": "m",
                "permission_mode": "default", "hook_event_name": "PreToolUse", "tool_name": "Bash",
                "tool_use_id": "call_1", "tool_input": {"command": command}}
    if harness == "pi":  # .pi/extensions/git-guard.ts: Pi's bash tool_call input and ctx.cwd
        return {"tool_name": "bash", "tool_input": {"command": command, "timeout": 120}, "cwd": cwd}
    # Hermes: .hermes/plugins/git-guard, shaped like Hermes's own shell-hook payload (agent/shell_hooks.py)
    return {"hook_event_name": "pre_tool_call", "tool_name": "terminal", "tool_input": {"command": command}, "cwd": cwd}


HARNESSES = ("claude", "codex", "pi", "hermes")


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
                    "tracking": os.path.join(root, "tracking"), "matching": os.path.join(root, "matching"), "plain": root,
                    "solo": os.path.join(root, "solo"), "tagged": os.path.join(root, "tagged")}
        feature, tracking, remote = cls.dirs["feature"], cls.dirs["tracking"], os.path.join(root, "remote.git")
        git("init", "-q", "-b", "main", main)
        git("init", "-q", "-b", "feat/x", feature)
        os.mkdir(os.path.join(main, "sub"))
        os.symlink(os.path.join(main, "sub"), os.path.join(feature, "link"))
        for name in ("--", "~"):  # bash's `cd --` and `cd ~` go to HOME, never to these
            os.mkdir(os.path.join(feature, name))
        git("-C", feature, "config", "remote.backup.url", remote)
        git("-C", feature, "config", "remote.backup.push", "HEAD:refs/heads/main")
        git("-C", feature, "config", "remote.mirror.url", remote)
        git("-C", feature, "config", "remote.mirror.mirror", "true")
        git("-C", feature, "config", "remote.paseo.url", remote)
        git("-C", feature, "config", "remote.paseo.push", "HEAD:refs/heads/feat/y")
        for name, key, value in (("matchall", "push", ":"), ("mirror2", "mirror", "2"), ("nomirror", "mirror", "false")):
            git("-C", feature, "config", f"remote.{name}.url", remote)
            git("-C", feature, "config", f"remote.{name}.{key}", value)
        git("init", "-q", "-b", "feat/x", cls.dirs["solo"])
        git("-C", cls.dirs["solo"], "config", "remote.backup.url", remote)
        git("-C", cls.dirs["solo"], "config", "remote.backup.push", "HEAD:refs/heads/main")
        git("init", "-q", "-b", "main", cls.dirs["tagged"])
        git("-C", cls.dirs["tagged"], "commit", "-q", "--no-verify", "--allow-empty", "-m", "init")
        git("-C", cls.dirs["tagged"], "tag", "main")
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
        for harness in HARNESSES:
            for command, where, expected in CASES:
                code, stderr = run_guard(json.dumps(payload(harness, command, self.dirs[where])))
                verdict = {0: "allow", 2: "block"}.get(code, f"exit {code}")
                counts[verdict] = counts.get(verdict, 0) + 1
                print(f"{harness:6} {verdict:5} [{where:8}] {command!r}" + (f"\n{'':23}{stderr}" if stderr else ""))
                with self.subTest(harness=harness, command=command):
                    self.assertEqual(verdict, expected, stderr)
                    if expected == "block":
                        self.assertIn("git-guard:", stderr)
        print(f"{len(CASES)} commands x {len(HARNESSES)} payload shapes: {counts}")

    def test_malformed_bash_payload_blocks(self):
        for body in MALFORMED:
            code, stderr = run_guard(body)
            with self.subTest(body=body[:60]):
                self.assertEqual(code, 2, stderr)
                self.assertIn("unreadable Bash hook payload", stderr)

    def test_other_tools_pass(self):
        for name in ("Read", "read_file"):
            code, _ = run_guard(json.dumps({"tool_name": name, "tool_input": {"command": "git push --force"}}))
            self.assertEqual(code, 0, name)

    def test_text_tools_screen_text_they_cannot_parse(self):
        merge_py = 'import subprocess\nsubprocess.run(["gh", "pr", "merge", "1175"])'
        for tool_input, tool, expected in (
            ({"command": "gh pr merge 1175"}, "powershell", 2),
            ({"command": "git -C $env:REPO push"}, "powershell", 2),
            ({"command": "Get-ChildItem"}, "powershell", 0),
            ({"code": merge_py}, "execute_code", 2),
            ({"code": "print(sum([1, 2]))"}, "execute_code", 0),
            ({"action": "submit", "session_id": "p1", "data": "gh pr merge 1175"}, "process_manage", 2),
            ({"action": "write", "session_id": "p1", "data": "git push origin HEAD:main\n"}, "process_manage", 2),
            ({"action": "submit", "session_id": "p1", "data": "ls"}, "process_manage", 0),
            ({"action": "kill", "session_id": "p1"}, "process_manage", 0),
            ({}, "execute_code", 2),
        ):
            code, stderr = run_guard(json.dumps({"tool_name": tool, "tool_input": tool_input}))
            self.assertEqual(code, expected, f"{tool} {tool_input}: {stderr}")
            if expected == 2 and tool_input:
                self.assertIn("cannot check that inside this tool", stderr)

    def test_hermes_workdir_picks_the_repo(self):
        for workdir, expected in ((self.dirs["main"], 2), (self.dirs["feature"], 0), ("", 2), (3, 2)):
            body = {**payload("hermes", "git commit -m x", self.dirs["plain"])}
            body["tool_input"] = {**body["tool_input"], "workdir": workdir}
            code, stderr = run_guard(json.dumps(body))
            self.assertEqual(code, expected, f"workdir={workdir!r}: {stderr}")

    def hermes_hook(self, terminal=None):
        """The plugin's pre_tool_call callback; terminal stands in for Hermes's tools.terminal_tool."""
        saved = {name: sys.modules.pop(name, None) for name in ("tools", "tools.terminal_tool")}

        def restore():
            for name, module in saved.items():
                sys.modules.pop(name, None)
                if module:
                    sys.modules[name] = module
        self.addCleanup(restore)
        if terminal:
            sys.modules["tools"] = types.ModuleType("tools")
            sys.modules["tools.terminal_tool"] = module = types.ModuleType("tools.terminal_tool")
            module._get_env_config = lambda: {"env_type": terminal.get("backend", "local"), "cwd": terminal["cwd"]}
            module.get_session_cwd = lambda task_id: terminal.get("session")
            module.resolve_task_overrides = lambda task_id: {}
        spec = importlib.util.spec_from_file_location("git_guard_hermes", HERMES_PLUGIN)
        plugin = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(plugin)
        hooks = {}
        plugin.register(type("Ctx", (), {"register_hook": lambda self, name, fn: hooks.__setitem__(name, fn)})())
        return hooks["pre_tool_call"]

    def test_hermes_plugin_blocks_through_the_guard(self):
        hook = self.hermes_hook({"cwd": self.dirs["feature"]})
        blocked = hook(tool_name="terminal", args={"command": "gh pr merge 1"}, task_id="")
        self.assertEqual(blocked["action"], "block")
        self.assertIn("git-guard:", blocked["message"])
        on_main = hook(tool_name="terminal", args={"command": "git commit -m x", "workdir": self.dirs["main"]}, task_id="")
        self.assertEqual(on_main["action"], "block")
        self.assertIsNone(hook(tool_name="terminal", args={"command": "git commit -m x"}, task_id=""))
        self.assertIsNone(hook(tool_name="read_file", args={"path": "x"}, task_id=""))
        merge_py = {"code": 'import subprocess\nsubprocess.run(["gh", "pr", "merge", "1175"])'}
        self.assertEqual(hook(tool_name="execute_code", args=merge_py, task_id="")["action"], "block")
        submit = {"action": "submit", "session_id": "p1", "data": "gh pr merge 1175"}
        self.assertEqual(hook(tool_name="process_manage", args=submit, task_id="")["action"], "block")

    def test_hermes_first_call_uses_the_configured_terminal_cwd(self):
        os.chdir(self.dirs["feature"])  # the Hermes process runs from a feature checkout ...
        self.addCleanup(os.chdir, ROOT)
        hook = self.hermes_hook({"cwd": self.dirs["main"]})  # ... but terminal.cwd points at main
        self.assertEqual(hook(tool_name="terminal", args={"command": "git commit -m x"}, task_id="")["action"], "block")
        hook = self.hermes_hook({"cwd": self.dirs["main"], "session": self.dirs["feature"]})  # a later call, after cd
        self.assertIsNone(hook(tool_name="terminal", args={"command": "git commit -m x"}, task_id=""))

    def test_hermes_refuses_writes_it_cannot_check(self):
        for terminal in ({"cwd": self.dirs["feature"], "backend": "docker"}, None):  # remote backend; no terminal module
            hook = self.hermes_hook(terminal)
            blocked = hook(tool_name="terminal", args={"command": "git commit -m x"}, task_id="")
            self.assertEqual(blocked["action"], "block", terminal)
            self.assertIn("cannot check", blocked["message"])
            self.assertIsNone(hook(tool_name="terminal", args={"command": "ls -la"}, task_id=""))

    def test_cdpath_makes_a_bare_cd_operand_ambiguous(self):
        env = {**os.environ, "CDPATH": self.tmp.name}
        for command, expected in (("cd feature && git commit -m x", 2), ("cd ./feature && git commit -m x", 0)):
            body = json.dumps(payload("claude", command, self.dirs["main"]))
            result = subprocess.run([sys.executable, GUARD], input=body, capture_output=True, text=True, env=env)
            self.assertEqual(result.returncode, expected, f"{command}: {result.stderr}")

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
