#!/usr/bin/env python3
"""PreToolUse hook for Claude Code and Codex: blocks agent git commands that take
a merge away from the maintainer or rewrite main (AGENTS.md, "Git / PR workflow").

Both harnesses send one JSON object on stdin with the shell string in
`tool_input.command` and the session directory in `cwd`; both block the call on
exit 2 and show stderr to the agent. Codex does not send `exec_command`'s
`workdir`, so a Codex call with a workdir is judged from the session `cwd`.

Not followed: commands built from variables, `xargs`/`find -exec`, git aliases.
"""
import json
import os
import re
import shlex
import subprocess
import sys

MAIN = "main"
MAIN_REFS = {MAIN, f"heads/{MAIN}", f"refs/heads/{MAIN}"}
SEPARATORS = {"&&", "||", ";", ";;", "|", "|&", "&", "(", ")", "\n"}
WRAPPERS = {"builtin", "command", "exec", "nohup", "time"}
SHELLS = {"bash", "dash", "sg", "sh", "zsh"}
GIT_VALUE_OPTIONS = {"-C", "-c", "--git-dir", "--work-tree", "--namespace", "--config-env"}
PUSH_VALUE_OPTIONS = {"--repo", "--receive-pack", "--exec", "--push-option"}
HEREDOC = re.compile(r"(<<-?[ \t]*(['\"]?)([A-Za-z_]\w*)\2[^\n]*)\n.*?^[ \t]*\3[ \t]*$", re.M | re.S)
MENTIONS_GIT = re.compile(r"\b(git|gh)\b")

PUSH_YOUR_BRANCH = "Run `git branch --show-current`, then `git push -u origin <that-branch>` (or plain `git push`)."
MESSAGES = {
    "merge": "only the maintainer merges a PR. Report the PR as ready instead.",
    "refspec": "a src:dst push can land one branch's commits on another branch (#900). " + PUSH_YOUR_BRANCH,
    "force": "a fix is a new commit, never a rewrite of pushed history. Commit the fix and push normally.",
    "all": "it pushes every local branch, main included. " + PUSH_YOUR_BRANCH,
    "push-main": "main changes only through a merged PR. Run `git switch -c <type>/<topic>`, then push that branch.",
    "commit-main": "main is protected. Run `git switch -c <type>/<topic>` first, then commit there.",
}


class Blocked(Exception):
    pass


def block(rule, shown):
    raise Blocked(f"`{shown}` is blocked: {MESSAGES[rule]}")


def segments(command):
    """Split a shell string into simple commands, dropping heredoc bodies and redirections."""
    lex = shlex.shlex(HEREDOC.sub(r"\1", command), posix=True, punctuation_chars=";&|()<>\n")
    lex.whitespace = " \t\r"
    lex.whitespace_split = True
    words = []
    skip_next = False
    for token in lex:
        if skip_next:
            skip_next = False
        elif token in SEPARATORS:
            yield words
            words = []
        elif set(token) <= set("<>&|") and set(token) & set("<>"):
            if words and words[-1].isdigit():
                words.pop()
            skip_next = True
        else:
            words.append(token)
    yield words


def unwrap(words):
    """Drop env assignments and wrappers such as `env`, `nohup`, `timeout 60`."""
    while words:
        head = words[0]
        if re.match(r"[A-Za-z_]\w*=", head) or head in WRAPPERS:
            words = words[1:]
        elif head in ("env", "timeout"):
            words = words[1:]
            while words and (words[0].startswith("-") or "=" in words[0]):
                words = words[2:] if words[0] in ("-u", "-s", "-k") else words[1:]
            if head == "timeout" and words:
                words = words[1:]
        else:
            return words
    return words


def resolve_dir(base, target):
    """Directory a `cd`/`-C` lands in, or None when it depends on runtime state."""
    if base is None or target == "-":
        return None
    path = os.path.expandvars(os.path.expanduser(target))
    if "$" in path:
        return None
    return os.path.normpath(os.path.join(base, path))


def git(cwd, options, *args):
    return subprocess.run(["git", *options, *args], cwd=cwd, capture_output=True, text=True)


def current_branch(cwd, options, shown):
    """Branch checked out in cwd; None for a detached HEAD or a directory outside any repo."""
    if cwd is None or not os.path.isdir(cwd):
        raise Blocked(
            f"cannot tell which branch `{shown}` runs on "
            f"({'a cd it cannot follow' if cwd is None else cwd + ' does not exist yet'}). "
            "Run the git command in its own call, from an existing directory or with `git -C <absolute dir>`."
        )
    result = git(cwd, options, "symbolic-ref", "--quiet", "--short", "HEAD")
    return result.stdout.strip() if result.returncode == 0 else None


def check_push(args, cwd, options, shown):
    positional = []
    i = 0
    while i < len(args):
        arg = args[i]
        i += 1
        if arg == "--":
            positional += args[i:]
            break
        if arg.startswith("--"):
            name = arg.split("=", 1)[0]
            if name in ("--force", "--mirror"):
                block("force", shown)
            if name in ("--all", "--branches"):
                block("all", shown)
            if name in PUSH_VALUE_OPTIONS and "=" not in arg:
                i += 1
        elif arg.startswith("-") and len(arg) > 1:
            flags = arg[1:]
            if "f" in flags.split("o", 1)[0]:
                block("force", shown)
            if flags.endswith("o"):
                i += 1
        else:
            positional.append(arg)

    refspecs = positional[1:]
    for ref in refspecs:
        if ref.startswith("+"):
            block("force", shown)
        if ":" in ref:
            block("refspec", shown)
        if ref in MAIN_REFS or (ref == "HEAD" and current_branch(cwd, options, shown) == MAIN):
            block("push-main", shown)
    if not refspecs:
        if current_branch(cwd, options, shown) == MAIN:
            block("push-main", shown)
        upstream = git(cwd, options, "rev-parse", "--symbolic-full-name", "@{push}").stdout.strip()
        if upstream.startswith("refs/remotes/") and upstream.endswith(f"/{MAIN}"):
            block("push-main", shown)


def check_git(args, cwd, shown):
    options = []
    i = 0
    while i < len(args) and args[i].startswith("-"):
        arg = args[i]
        if arg in GIT_VALUE_OPTIONS and i + 1 < len(args):
            if arg == "-C":
                cwd = resolve_dir(cwd, args[i + 1])
            else:
                options += args[i : i + 2]
            i += 2
            continue
        if arg.split("=", 1)[0] in GIT_VALUE_OPTIONS:
            options.append(arg)
        i += 1
    if i >= len(args):
        return
    command, rest = args[i], args[i + 1 :]
    if command == "commit" and current_branch(cwd, options, shown) == MAIN:
        block("commit-main", shown)
    if command == "push":
        check_push(rest, cwd, options, shown)


def check_gh(args, shown):
    words = []
    i = 0
    while i < len(args):
        if args[i] in ("-R", "--repo"):
            i += 2
            continue
        if not args[i].startswith("-"):
            words.append(args[i])
        i += 1
    if words[:2] == ["pr", "merge"]:
        block("merge", shown)
    if words[:1] == ["api"] and any(re.search(r"/pulls/[^/]+/merge\b", w) for w in words):
        block("merge", shown)


def check(command, cwd):
    for words in segments(command):
        words = unwrap(words)
        if not words:
            continue
        program = os.path.basename(words[0])
        shown = " ".join(words)
        if program == "cd":
            cwd = resolve_dir(cwd, words[1] if len(words) > 1 else "~")
        elif program == "eval":
            check(" ".join(words[1:]), cwd)
        elif program in SHELLS:
            for flag, script in zip(words[1:], words[2:]):
                if re.fullmatch(r"-[a-z]*c[a-z]*", flag):
                    check(script, cwd)
                    break
        elif program == "git":
            check_git(words[1:], cwd, shown)
        elif program == "gh":
            check_gh(words[1:], shown)


def main():
    try:
        payload = json.load(sys.stdin)
        command = payload["tool_input"]["command"]
    except (ValueError, KeyError, TypeError):
        return 0
    if isinstance(command, list):
        command = shlex.join(command)
    if not isinstance(command, str):
        return 0
    try:
        check(command, payload.get("cwd") or os.getcwd())
    except Blocked as reason:
        print(f"git-guard: {reason}", file=sys.stderr)
        return 2
    except Exception as error:  # an unparseable git/gh command fails closed; anything else passes
        if MENTIONS_GIT.search(command):
            print(f"git-guard: cannot parse this git/gh command ({error}); split it into simpler calls.", file=sys.stderr)
            return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
