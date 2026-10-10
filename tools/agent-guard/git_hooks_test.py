#!/usr/bin/env python3
"""Table test for .githooks/pre-commit and .githooks/pre-push (#1171), run with real git
against local bare repositories. No network.

Run: python3 tools/agent-guard/git_hooks_test.py
"""
import os
import subprocess
import sys
import tempfile
import unittest

HOOKS = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), ".githooks")
# Hook-exported variables (GIT_DIR and friends) must not leak in when this runs under a hook itself.
ENV = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}


def run(cwd, *args, check=False):
    result = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True, env=ENV)
    if check and result.returncode != 0:
        raise AssertionError(f"git {' '.join(args)} failed: {result.stderr}")
    return result


def head_of(bare, ref):
    return run(bare, "rev-parse", "--verify", "--quiet", ref).stdout.strip()


class Repo:
    """A clone of a bare remote with the repo's hooks on, on branch feat/x, main one commit ahead."""

    def __init__(self, root):
        self.root = root
        self.bare = os.path.join(root, "remote.git")
        self.work = os.path.join(root, "work")
        run(root, "init", "-q", "--bare", "-b", "main", self.bare, check=True)
        run(root, "init", "-q", "-b", "main", self.work, check=True)
        for key, value in (("user.name", "t"), ("user.email", "t@t"), ("core.hooksPath", HOOKS),
                           ("cluckwork.hookTests", "false")):
            run(self.work, "config", key, value, check=True)
        run(self.work, "remote", "add", "origin", self.bare, check=True)
        self.commit("base", hooks=False)
        run(self.work, "push", "-q", "--no-verify", "origin", "main", check=True)
        run(self.work, "push", "-q", "--no-verify", "origin", "main:refs/heads/feat/old", check=True)
        run(self.work, "tag", "v1", check=True)
        self.commit("main ahead", hooks=False)
        run(self.work, "switch", "-q", "-c", "feat/x", check=True)
        self.commit("feature", hooks=False)

    def commit(self, message, hooks=True, cwd=None):
        cwd = cwd or self.work
        path = os.path.join(cwd, "notes.txt")
        with open(path, "a") as f:
            f.write(message + "\n")
        run(cwd, "add", "notes.txt", check=True)
        return run(cwd, "commit", "-q", "-m", f"chore: {message}", *([] if hooks else ["--no-verify"]))


class PrePushTest(unittest.TestCase):
    # (setup, push arguments, expected). Setup names a method that prepares the clone first.
    CASES = [
        (None, ["origin", "feat/x"], "allow"),
        (None, ["-u", "origin", "HEAD"], "allow"),
        ("pushed_then_ahead", ["origin", "feat/x"], "allow"),
        (None, ["origin", "v1"], "allow"),
        (None, ["origin", "--delete", "feat/old"], "allow"),
        ("paseo_remote", ["paseo"], "allow"),
        (None, ["origin", "main"], "refuse"),
        (None, ["origin", "feat/x:main"], "refuse"),
        (None, ["origin", "HEAD:refs/heads/main"], "refuse"),
        (None, ["origin", "--delete", "main"], "refuse"),
        (None, ["origin", ":main"], "refuse"),
        (None, ["origin", "feat/x:feat/y"], "refuse"),
        (None, ["origin", "HEAD:feat/y"], "refuse"),
        (None, ["origin", "HEAD~0:refs/heads/feat/x"], "refuse"),
        ("paseo_remote", ["paseo", "HEAD:refs/heads/feat/z"], "refuse"),
        ("pushed_then_rewritten", ["--force", "origin", "feat/x"], "refuse"),
        ("pushed_then_rewritten", ["origin", "+feat/x"], "refuse"),
        ("pushed_then_rewritten", ["--force-with-lease", "origin", "feat/x"], "refuse"),
        ("on_main", ["origin", "HEAD"], "refuse"),
        ("on_main", [], "refuse"),
    ]

    @staticmethod
    def pushed_then_ahead(repo):
        run(repo.work, "push", "-q", "--no-verify", "origin", "feat/x", check=True)
        repo.commit("more", hooks=False)

    @staticmethod
    def pushed_then_rewritten(repo):
        run(repo.work, "push", "-q", "--no-verify", "origin", "feat/x", check=True)
        run(repo.work, "commit", "-q", "--amend", "--no-verify", "-m", "rewritten", check=True)

    @staticmethod
    def paseo_remote(repo):
        run(repo.work, "switch", "-q", "-c", "feat/y-1", check=True)
        run(repo.work, "remote", "add", "paseo", repo.bare, check=True)
        run(repo.work, "config", "remote.paseo.push", "HEAD:refs/heads/feat/y", check=True)

    @staticmethod
    def on_main(repo):
        run(repo.work, "switch", "-q", "main", check=True)
        run(repo.work, "branch", "-q", "--set-upstream-to=origin/main", check=True)

    def test_table(self):
        for setup, args, expected in self.CASES:
            with tempfile.TemporaryDirectory() as root, self.subTest(push=" ".join(args), setup=setup):
                repo = Repo(root)
                if setup:
                    getattr(self, setup)(repo)
                before = {ref: head_of(repo.bare, ref) for ref in ("refs/heads/main", "refs/heads/feat/x")}
                result = run(repo.work, "push", "-q", *args)
                verdict = "allow" if result.returncode == 0 else "refuse"
                print(f"pre-push   {verdict:6} [{setup or 'feat/x':21}] git push {' '.join(args)}"
                      + (f"\n{'':39}{result.stderr.strip().splitlines()[0]}" if verdict == "refuse" else ""))
                self.assertEqual(verdict, expected, result.stderr)
                if expected == "refuse":
                    self.assertIn("pre-push: refusing", result.stderr)
                    after = {ref: head_of(repo.bare, ref) for ref in before}
                    self.assertEqual(after, before, "a refused push changed the remote")


class PreCommitTest(unittest.TestCase):
    def check(self, label, result, expected):
        verdict = "allow" if result.returncode == 0 else "refuse"
        print(f"pre-commit {verdict:6} [{label}]" + (f"\n{'':39}{result.stderr.strip()}" if verdict == "refuse" else ""))
        self.assertEqual(verdict, expected, result.stderr)
        if expected == "refuse":
            self.assertIn("pre-commit: refusing", result.stderr)

    def test_branches(self):
        with tempfile.TemporaryDirectory() as root:
            repo = Repo(root)
            self.check("feat/x", repo.commit("on feature"), "allow")
            run(repo.work, "switch", "-q", "main", check=True)
            self.check("main", repo.commit("on main"), "refuse")
            ENV["SKIP_HOOKS"] = "1"
            try:
                self.check("main, SKIP_HOOKS=1", repo.commit("skip"), "refuse")
            finally:
                del ENV["SKIP_HOOKS"]
            run(repo.work, "reset", "-q", "--hard", check=True)  # drop the refused commits' staged notes
            run(repo.work, "switch", "-q", "--detach", "feat/x", check=True)
            self.check("detached HEAD", repo.commit("detached"), "allow")

    def test_linked_worktrees_see_their_own_branch(self):
        with tempfile.TemporaryDirectory() as root:
            repo = Repo(root)  # main checkout on feat/x
            on_main = os.path.join(root, "wt-main")
            run(repo.work, "worktree", "add", "-q", on_main, "main", check=True)
            self.check("linked worktree on main", repo.commit("wt main", cwd=on_main), "refuse")
            self.check("main checkout on feat/x beside it", repo.commit("checkout feature"), "allow")
            run(on_main, "switch", "-q", "-c", "feat/wt", check=True)
            run(repo.work, "switch", "-q", "main", check=True)
            self.check("linked worktree on feat/wt, main checkout on main", repo.commit("wt feature", cwd=on_main), "allow")
            self.check("main checkout on main", repo.commit("checkout main"), "refuse")

    def test_tests_skipped_when_hook_tests_off(self):
        with tempfile.TemporaryDirectory() as root:
            repo = Repo(root)
            with open(os.path.join(repo.work, "Thing.cs"), "w") as f:
                f.write("class Thing {}\n")
            run(repo.work, "add", "Thing.cs", check=True)
            result = run(repo.work, "commit", "-q", "-m", "chore: cs change")
            self.check("feat/x, .cs staged, cluckwork.hookTests=false", result, "allow")
            self.assertNotIn(".NET unit tests", result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
