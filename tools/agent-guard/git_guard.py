#!/usr/bin/env python3
"""PreToolUse hook for Claude Code and Codex: blocks agent git commands that take
a merge away from the maintainer or rewrite main (AGENTS.md, "Git / PR workflow").

Both harnesses send one JSON object on stdin with the shell string in
`tool_input.command` and the session directory in `cwd`; both block the call on
exit 2 and show stderr to the agent.

A command that mentions a git write (`git push`, `git commit`) or a PR merge must
be one simple command, optionally after `cd <dir> &&`. Anything else is refused
rather than parsed, so the agent re-runs the write on its own.

Not covered: gh aliases defined earlier, a GraphQL merge whose query comes from a
file, git aliases from config, other commands that create commits (merge, rebase,
cherry-pick, pull), and Codex `exec_command` calls whose `workdir` differs from
the session `cwd` (Codex does not send `workdir`).
"""
import json
import os
import re
import shlex
import subprocess
import sys
import urllib.parse

MAIN = "main"
MAIN_REFS = {MAIN, f"heads/{MAIN}", f"refs/heads/{MAIN}"}
MENTIONS_WRITE = re.compile(r"\bgit\b.*\b(push|commit)\b|\bgh\b.*\b(merge|alias)\b|mergePullRequest|AutoMerge", re.S)
MENTIONS_TOOL_OR_VERB = re.compile(r"\b(git|gh|push|commit|merge|alias)\b|mergePullRequest|AutoMerge")
EXPANSIONS = re.compile(r"[$`*?\[]")  # can spell a program or verb the text check cannot read
INERT_PROGRAMS = {"cat", "echo", "egrep", "fgrep", "grep", "head", "ls", "printf", "tail", "wc"}
REDIRECTS = {">", ">>", ">&", "&>", "&>>"}
GIT_GLOBAL_FLAGS = {"--no-pager", "-P", "-p", "--paginate", "--no-optional-locks", "--literal-pathspecs", "--no-replace-objects"}
GIT_GLOBAL_VALUES = {"-C", "-c", "--git-dir", "--work-tree"}
PUSH_LONG_OPTIONS = [
    "all", "atomic", "branches", "delete", "dry-run", "exec", "follow-tags", "force", "force-if-includes",
    "force-with-lease", "ipv4", "ipv6", "mirror", "porcelain", "progress", "prune", "push-option", "quiet",
    "receive-pack", "recurse-submodules", "repo", "set-upstream", "signed", "tags", "thin", "verbose", "verify",
]
GH_API_VALUES = {"-X", "--method", "-H", "--header", "-f", "--raw-field", "-F", "--field", "--input",
                 "-q", "--jq", "-t", "--template", "--hostname", "-p", "--preview", "--cache"}
GH_API_FLAGS = {"--paginate", "-i", "--include", "--silent", "--verbose", "--slurp"}
GH_API_BODY = {"-f", "--raw-field", "-F", "--field", "--input"}

PUSH_YOUR_BRANCH = "Run `git branch --show-current`, then `git push -u origin <that-branch>` (or plain `git push`)."
MESSAGES = {
    "simple": (
        "it contains, or may spell, a git write or PR merge, so it must run as its own simple command: one command, optionally "
        "after `cd <dir> &&`, with no `;`, `||`, `|`, `&`, newlines, comments, subshells, braces, if/for/while, "
        "heredocs, `$(…)`, backticks, variables, unquoted globs, env assignments or wrappers (env, command, timeout, sudo, bash -c). "
        "Allowed forms: `git push -u origin <branch>`, `cd <dir> && git commit -m \"<message>\"`. "
        "For a multi-line message use `git commit -F <file>` or repeated `-m`."
    ),
    "merge": "only the maintainer merges a PR. Report the PR as ready instead.",
    "alias": "a gh alias can hide `gh pr merge`. Run the gh command it stands for directly.",
    "git-config": "a `-c` override of alias.*, push.*, remote.* or branch.* can change what the command does. "
                  "Run it without the override.",
    "refspec": "a src:dst push can land one branch's commits on another branch (#900). " + PUSH_YOUR_BRANCH,
    "force": "a fix is a new commit, never a rewrite of pushed history. Commit the fix and push normally.",
    "all": "it pushes or deletes more than your branch (--all, --branches, --prune). " + PUSH_YOUR_BRANCH,
    "push-main": "main changes only through a merged PR. Run `git switch -c <type>/<topic>`, then push that branch.",
    "push-config": "the push configuration (push.default=matching, remote.<name>.mirror, or a forced, wildcard "
                   "or main remote.<name>.push) can update main or force. Push to a remote without it.",
    "upstream-main": "with push.default=upstream this branch would push to its upstream, main. "
                     "Run `git branch --unset-upstream`, then `git push -u origin <that-branch>`.",
    "commit-main": "main is protected. Run `git switch -c <type>/<topic>` first, then commit there.",
    "option": "git-guard does not know this option, so it cannot tell what the write does. Drop it or spell it out.",
}


class Blocked(Exception):
    pass


def block(rule, shown):
    raise Blocked(f"`{shown}` is blocked: {MESSAGES[rule]}")


def has_simple_shape(command):
    """True when every unquoted shell operator is `&&` or an output redirection."""
    quote = None
    i = 0
    while i < len(command):
        c = command[i]
        if c == "\\" and quote != "'":
            if command[i + 1 : i + 2] in ("", "\n"):
                return False
            i += 2
            continue
        if quote == "'":
            quote = None if c == "'" else quote
        elif c in "`$":
            return False
        elif quote == '"':
            quote = None if c == '"' else quote
        elif c in "'\"":
            quote = c
        elif c in "\n;|(){}<*?[" or (c == "#" and (i == 0 or command[i - 1] in " \t")):
            return False
        i += 1
    return True  # an unclosed quote fails in shlex below


def simple_command(command, cwd):
    """(argv, cwd) for `[cd <dir> &&] <argv> [redirections]`; block any other shape."""
    if not has_simple_shape(command):
        block("simple", command)
    lex = shlex.shlex(command, posix=True, punctuation_chars="&>")
    lex.whitespace_split = True
    lex.commenters = ""
    parts = [[]]
    for token in lex:
        if token == "&&":
            parts.append([])
        else:
            parts[-1].append(token)
    if len(parts) > 2 or not all(parts) or (len(parts) == 2 and (parts[0][0] != "cd" or len(parts[0]) != 2)):
        block("simple", command)
    if len(parts) == 2:
        cwd = resolve_dir(cwd, parts[0][1])
    argv = []
    tokens = iter(parts[-1])
    for token in tokens:
        if token in REDIRECTS:
            target = next(tokens, None)
            if target is None or set(target) <= set("&>"):
                block("simple", command)
            if argv and argv[-1].isdigit():
                argv.pop()
        elif set(token) <= set("&>"):
            block("simple", command)
        else:
            argv.append(token)
    if not argv:
        block("simple", command)
    return argv, cwd


def resolve_dir(base, target):
    """Directory a `cd`/`-C` lands in, or None when it depends on runtime state."""
    if base is None or target == "-":
        return None
    return os.path.normpath(os.path.join(base, os.path.expanduser(target)))


class Repo:
    """Read-only git lookups in the directory the command runs in; a failed lookup blocks."""

    def __init__(self, cwd, options, shown):
        if cwd is None or not os.path.isdir(cwd):
            raise Blocked(f"`{shown}` is blocked: git-guard cannot find the directory it runs in. "
                          "Run it from an existing directory, or with `cd <absolute dir> &&`.")
        self.cwd, self.options, self.shown = cwd, options, shown

    def git(self, *args):
        return subprocess.run(["git", *self.options, *args], cwd=self.cwd, capture_output=True, text=True)

    def lookup_failed(self, what, result):
        return Blocked(f"`{self.shown}` is blocked: git-guard could not read {what} in {self.cwd} "
                       f"({result.stderr.strip() or 'exit ' + str(result.returncode)}). Fix that, then retry.")

    def branch(self):
        """Checked-out branch, or None for a detached HEAD."""
        result = self.git("symbolic-ref", "--quiet", "--short", "HEAD")
        if result.returncode not in (0, 1):
            raise self.lookup_failed("the current branch", result)
        return result.stdout.strip() or None

    def config(self):
        """Effective config, `-c` overrides included: key -> values (None for a bare boolean)."""
        result = self.git("config", "--list", "-z")
        if result.returncode != 0:
            raise self.lookup_failed("git config", result)
        entries = {}
        for entry in filter(None, result.stdout.split("\0")):
            key, _, value = entry.partition("\n")
            entries.setdefault(key, []).append(value if "\n" in entry else None)
        return entries


def long_option(arg, shown):
    """Full name of a push long option, resolving git's unambiguous prefixes."""
    name = arg[2:].split("=", 1)[0]
    negated = name.startswith("no-")
    base = name[3:] if negated else name
    matches = [base] if base in PUSH_LONG_OPTIONS else [o for o in PUSH_LONG_OPTIONS if o.startswith(base)]
    if len(matches) != 1:
        block("option", shown)
    return ("no-" if negated else "") + matches[0]


def check_push(args, repo):
    shown = repo.shown
    positional = []
    i = 0
    while i < len(args):
        arg = args[i]
        i += 1
        if arg == "--":
            positional += args[i:]
            break
        if arg.startswith("--"):
            name = long_option(arg, shown)
            if name in ("force", "mirror"):
                block("force", shown)
            if name in ("all", "branches", "prune"):
                block("all", shown)
            if name in ("repo", "receive-pack", "exec"):
                block("option", shown)
            if name == "push-option" and "=" not in arg:
                i += 1
        elif arg.startswith("-") and len(arg) > 1:
            for j, flag in enumerate(arg[1:], start=1):
                if flag == "f":
                    block("force", shown)
                if flag == "o":
                    i += 0 if arg[j + 1 :] else 1
                    break
        else:
            positional.append(arg)

    branch = repo.branch()
    config = repo.config()

    def setting(key, default=None):
        return (config.get(key) or [default])[-1]

    remote = positional[0] if positional else (
        setting(f"branch.{branch}.pushremote") or setting("remote.pushdefault")
        or setting(f"branch.{branch}.remote") or "origin")
    push_default = (setting("push.default") or "simple").lower()
    mirror = setting(f"remote.{remote}.mirror", "false")
    if push_default == "matching" or mirror is None or mirror.lower() in ("true", "yes", "on", "1"):
        block("push-config", shown)
    for configured in config.get(f"remote.{remote}.push") or []:
        if not configured or configured.startswith("+") or "*" in configured or configured.split(":")[-1] in MAIN_REFS:
            block("push-config", shown)

    sources = []
    for ref in positional[1:]:
        if ref.startswith("+"):
            block("force", shown)
        if ":" in ref:
            block("refspec", shown)
        sources.append(branch if ref in ("HEAD", "@") else ref.removeprefix("refs/heads/"))
    for source in sources or [branch]:
        if source in MAIN_REFS:
            block("push-main", shown)
        if push_default in ("upstream", "tracking") and setting(f"branch.{source}.merge") in MAIN_REFS:
            block("upstream-main", shown)


def check_git(args, cwd, shown):
    options = []
    i = 0
    while i < len(args) and args[i].startswith("-"):
        arg = args[i]
        name = arg.split("=", 1)[0]
        if arg in GIT_GLOBAL_VALUES and i + 1 < len(args):
            value = args[i + 1]
            if arg == "-c" and re.match(r"(alias|push|remote|branch)\.", value, re.I):
                block("git-config", shown)
            if arg == "-C":
                cwd = resolve_dir(cwd, value)
            else:
                options += [arg, value]
            i += 2
        elif arg in GIT_GLOBAL_FLAGS or (name in ("--git-dir", "--work-tree") and "=" in arg):
            options.append(arg)
            i += 1
        else:
            block("option", shown)
    if i >= len(args) or args[i] not in ("commit", "push"):
        return
    repo = Repo(cwd, options, shown)
    if args[i] == "push":
        check_push(args[i + 1 :], repo)
    elif repo.branch() == MAIN:
        block("commit-main", shown)


def check_gh_api(args, shown):
    method = None
    fields = []
    positional = []
    i = 0
    while i < len(args):
        arg = args[i]
        i += 1
        if arg in GH_API_FLAGS:
            continue
        if arg.startswith("--") and "=" in arg:
            name, value = arg.split("=", 1)
        elif re.fullmatch(r"-[A-Za-z].+", arg):
            name, value = arg[:2], arg[2:]
        elif arg in GH_API_VALUES and i < len(args):
            name, value = arg, args[i]
            i += 1
        elif arg.startswith("-"):
            block("option", shown)
        else:
            positional.append(arg)
            continue
        if name not in GH_API_VALUES:
            block("option", shown)
        if name in ("-X", "--method"):
            method = value.upper()
        if name in GH_API_BODY:
            fields.append(value)
    method = method or ("POST" if fields else "GET")
    endpoint = urllib.parse.unquote(positional[0]) if positional else ""
    if method != "GET" and re.search(r"(^|/)pulls/[^/]+/merge(?![\w.-])", endpoint):
        block("merge", shown)
    if endpoint == "graphql" and any(re.search(r"mergePullRequest|AutoMerge", f) for f in fields):
        block("merge", shown)


def check_gh(args, shown):
    words = []  # (index, word) of the first two non-flag arguments
    i = 0
    while i < len(args) and len(words) < 2:
        if args[i] in ("-R", "--repo"):
            i += 2
            continue
        if not args[i].startswith("-"):
            words.append((i, args[i]))
        i += 1
    names = [word for _, word in words]
    if names == ["pr", "merge"]:
        block("merge", shown)
    if names[:1] == ["alias"] and names[1:] != ["list"]:
        block("alias", shown)
    if names[:1] == ["api"]:
        check_gh_api(args[words[0][0] + 1 :], shown)


def mentions_write(command):
    """Text check before parsing: quotes, backslashes and %-escapes undone, as `pu""sh` and `merg%65` would be.
    An expansion beside git, gh or a write verb counts too, since it can spell the rest (`/usr/bin/gi? push`)."""
    plain = urllib.parse.unquote(re.sub(r"[\"'\\]", "", command))
    return MENTIONS_WRITE.search(plain) or (EXPANSIONS.search(command) and MENTIONS_TOOL_OR_VERB.search(plain))


def check(command, cwd):
    if not mentions_write(command):
        return
    argv, cwd = simple_command(command, cwd)
    program = os.path.basename(argv[0])
    shown = " ".join(argv)
    if program == "git":
        check_git(argv[1:], cwd, shown)
    elif program == "gh":
        check_gh(argv[1:], shown)
    elif program not in INERT_PROGRAMS:
        block("simple", shown)


def read_payload(text):
    """(command, cwd) from a Bash PreToolUse payload, or None for another tool."""
    payload = json.loads(text)
    if payload.get("tool_name", "Bash") != "Bash":
        return None
    tool_input = payload.get("tool_input")
    command = tool_input.get("command") if isinstance(tool_input, dict) else None
    cwd = payload.get("cwd") or os.getcwd()
    if not isinstance(command, str) or not isinstance(cwd, str):
        raise ValueError("tool_input.command or cwd is missing or not a string")
    return command, cwd


def main():
    try:
        request = read_payload(sys.stdin.read())
    except Exception as error:
        print(f"git-guard: unreadable Bash hook payload ({type(error).__name__}: {str(error)[:200]}). "
              "The harness payload format may have changed; fix tools/agent-guard/git_guard.py.", file=sys.stderr)
        return 2
    if request is None:
        return 0
    try:
        check(*request)
    except Blocked as reason:
        print(f"git-guard: {reason}", file=sys.stderr)
        return 2
    except Exception as error:  # any failure while checking a command that mentions a write blocks it
        print(f"git-guard: cannot check this command ({type(error).__name__}: {error}). "
              "Run the git write as its own simple command.", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
