#!/usr/bin/env python3
"""Pre-command hook for Claude Code, Codex, Pi and Hermes: blocks agent git commands
that take a merge away from the maintainer or rewrite main (AGENTS.md, "Git / PR workflow").

Every harness sends one JSON object on stdin with the shell string in
`tool_input.command` and a directory in `cwd`, and blocks the call on exit 2 with
stderr shown to the agent. The shell tool's name differs: `Bash` (Claude Code,
Codex), `bash` (`.pi/extensions/git-guard.ts`), `terminal` (Hermes, through
`.hermes/plugins/git-guard`). Hermes may add `tool_input.workdir`, the
directory that one command runs in, and the plugin adds `unchecked` when it
cannot say which repository the terminal runs in; a git write or merge is then
refused. Hermes's `process_manage` write/submit input is shell input to a running
process: it gets the same check with no known directory. Tools whose text this
guard cannot parse (Hermes `execute_code`, Pi `powershell`) are refused outright.

A command that mentions a git write (`git push`, `git commit`, a subcommand that
changes a checkout, a shallow fetch) or a PR merge must be one simple command,
optionally after `cd <dir> &&`. Anything else is refused rather than parsed, so
the agent re-runs the write on its own. The one exception is a chain whose every
step is a `gh api` GET or a READ_FILTERS program, since a read of a write route is
still a read.

The shared checkout is a main worktree that has linked worktrees: agents work in
the linked ones, and all of them share its `.git` (#1205). There, only the
SHARED_CHECKOUT_SAFE subcommands run. In every worktree, `git stash` and shallow
fetches are refused, because the stash list and the shallow state are shared too.
GIT_GUARD_ALLOW_SHARED=1 lifts those three rules wherever it is set. It is for the
maintainer, on one command in their own shell (CONTRIBUTING.md); set for a harness,
every agent started from it would inherit it.

Also refused (#1221): a non-GET `gh api` call on `merges`, `contents`, `git/refs`,
`rulesets` or `branches/<b>/protection` (explicit `-X`/`--method`, or the POST that
`-f`/`-F`/`--input` imply), `git send-pack`, `git update-ref` on a main ref,
`git config` writing `alias.*` or `core.hooksPath` in any scope, and
`git push --no-verify`. Also a `gh api graphql` call whose query, inline or read from `-F key=@file`
or `--input file`, names a mutation that merges, commits, moves a ref or changes a
ruleset or branch protection rule (GRAPHQL_WRITES); an unreadable query file blocks.

Not covered: gh aliases defined earlier, git aliases already in config, `git config
--remove-section`, other commands that move a local ref (branch -f, fetch -f) or
create commits (merge, rebase, cherry-pick, pull), a verb spelled by brace or
extglob expansion (`pu{s..s}h`, `p@(u)sh`), a configured push destination that
the remote resolves as a symbolic ref to main (no remote lookups), and Codex
`exec_command` calls whose `workdir` differs from the session `cwd` (Codex does
not send `workdir`).
"""
import json
import os
import re
import shlex
import stat
import subprocess
import sys
import urllib.parse

MAIN = "main"
SHELL_TOOLS = {"Bash", "bash", "terminal"}  # Claude Code and Codex, the Pi extension, Hermes
# Tools that run text this guard cannot parse are refused outright, naming the checked tool to use.
REFUSED_TOOLS = {
    "execute_code": "use the terminal tool for shell and git commands; git-guard cannot check Python code.",
    "powershell": "use the bash tool; git-guard cannot check PowerShell.",
}
MAIN_REFS = {MAIN, f"heads/{MAIN}", f"refs/heads/{MAIN}"}
GIT_FALSE = {"false", "no", "off", "0", ""}
# gh words that can merge, commit, move a ref or change main's protection: `gh pr merge`, `gh alias`, and the
# REST paths GH_API_PROTECTED names.
GH_WRITE_WORDS = r"\b(?:merges?|alias|rulesets)\b|/(?:contents|protection)\b|git/refs"
# GraphQL mutations that merge, commit, move a ref or change main's protection.
GRAPHQL_WRITES = (r"mergePullRequest|AutoMerge|mergeBranch|createCommitOnBranch"
                  r"|(?:create|update|delete)(?:Refs?\b|RepositoryRuleset|BranchProtectionRule)")
# A gh body read from a file or stdin (`-F key=@file`, `--input file`) is text the command does not show.
MENTIONS_WRITE = re.compile(rf"\bgit\b.*\b(push|commit|send-pack)\b|\bgh\b.*(?:{GH_WRITE_WORDS}|=@|--input\b)|{GRAPHQL_WRITES}", re.S)
MENTIONS_TOOL_OR_VERB = re.compile(rf"\b(git|gh|push|commit|merge|alias|send-pack|update-ref)\b|(?i:hookspath)|{GH_WRITE_WORDS}"
                                   rf"|{GRAPHQL_WRITES}")
# A git subcommand that changes HEAD, the index, the files or a ref, right after `git` and its options.
CHANGES_CHECKOUT = re.compile(r"\bgit(?:\s+-\S+(?:\s+[^-\s]\S*)?)*\s+(?:add|am|apply|checkout|cherry-pick|clean|merge|"
                              r"mv|notes|pull|rebase|reset|restore|revert|rm|stash|switch|tag|update-ref)(?![\w-])")
# fetch/pull long options that change the shallow state; git also takes any unambiguous prefix (--dep=1).
SHALLOW_OPTIONS = ("depth", "deepen", "shallow-since", "shallow-exclude", "unshallow", "update-shallow")
SHALLOW = re.compile(r"\b(?:fetch|pull)\b.*\s--(?:de|sh|un|up)", re.S)
# What coordinators run in the shared checkout: reads, plain fetch, worktree and branch management.
# writes_anyway() refuses the forms of these that still write there.
SHARED_CHECKOUT_SAFE = {"blame", "branch", "cat-file", "config", "describe", "diff", "fetch", "for-each-ref", "grep",
                        "help", "log", "ls-files", "ls-tree", "merge-tree", "remote", "rev-list", "rev-parse",
                        "shortlog", "show", "status", "version", "worktree"}
GIT_WORD = re.compile(r"\bgit\b")
ASSIGNMENT = re.compile(r"[A-Za-z_]\w*=")
# Text that can move a git command's target to another checkout.
DIRECTORY_CHANGE = re.compile(r"(?:^|[\s;&|(){}])(?:cd|pushd|popd)\b|\s-C\b|--git-dir|--work-tree|\bGIT_(?:DIR|WORK_TREE)\b")
EXPANSIONS = re.compile(r"[$`*?\[]")  # can spell a program or verb the text check cannot read
BRACED_WORD = re.compile(r"\{[\w-]+\}")
ALLOWED_SUFFIX = re.compile(r"[ \t]+(2>&1|2>/dev/null)$")
INERT_PROGRAMS = {"cat", "echo", "egrep", "fgrep", "grep", "head", "ls", "printf", "tail", "wc"}
# Programs whose arguments are only data: a `git` among them is text, not a command. Any other program whose
# arguments name git may run it (xargs, env, sh -c, python -c, make, ...), so its target is unresolved.
DATA_PROGRAMS = INERT_PROGRAMS | {"column", "comm", "cut", "diff", "fold", "jq", "nl", "rg", "sort", "tee", "tr", "uniq"}
DATA_PROGRAM_RUNNERS = ("--pre", "--hostname-bin", "--compress-program")  # rg and sort options that run a program
# Filters a chain of `gh api` GETs may pipe through: each only reads its input, writes no file and runs no
# program under any option. sort is out: -o writes and --compress[-program] runs one on a spill.
READ_FILTERS = {"cat", "grep", "head", "jq", "tail", "wc"}
GIT_GLOBAL_FLAGS = {"--no-pager", "-P", "-p", "--paginate", "--no-optional-locks", "--literal-pathspecs", "--no-replace-objects",
                    "--version", "-v", "--help", "-h", "--exec-path", "--html-path", "--man-path", "--info-path",
                    "--no-advice", "--no-lazy-fetch", "--glob-pathspecs", "--noglob-pathspecs", "--icase-pathspecs"}
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
# REST paths whose non-GET methods merge, commit, move a ref or change main's protection outside a PR.
GH_API_PROTECTED = re.compile(r"(^|/)(merges|contents|git/refs|rulesets|branches/[^/]+/protection)(/|$)")
# `git config` long options by role; the get/list subcommands count only in the operation position.
CONFIG_READS = {"get", "get-all", "get-regexp", "get-urlmatch", "list"}
CONFIG_WRITES = {"add", "replace-all", "unset", "unset-all"}
CONFIG_VALUES = {"file", "blob", "comment", "default", "type", "url", "value"}  # each consumes the next token
CONFIG_LONG_OPTIONS = [
    "add", "all", "blob", "bool", "bool-or-int", "bool-or-str", "comment", "default", "edit", "expiry-date", "file",
    "fixed-value", "get", "get-all", "get-color", "get-colorbool", "get-regexp", "get-urlmatch", "global", "includes",
    "int", "list", "local", "name-only", "null", "path", "regexp", "remove-section", "rename-section", "replace-all",
    "show-names", "show-origin", "show-scope", "system", "type", "unset", "unset-all", "url", "value", "worktree",
]
CONFIG_GUARDED_KEY = re.compile(r"alias\.|core\.hookspath$", re.I)

PUSH_YOUR_BRANCH = "Run `git branch --show-current`, then `git push -u origin <that-branch>` (or plain `git push`)."
MESSAGES = {
    "simple": (
        "it contains, or may spell, a git write or PR merge, so it must run as its own simple command: one command, optionally "
        "after `cd <dir> &&`, with no `;`, `||`, `|`, `&`, newlines, comments, subshells, braces, if/for/while, "
        "heredocs, redirections other than a trailing `2>&1` or `2>/dev/null`, `$(…)`, backticks, variables, "
        "unquoted globs, env assignments or wrappers (env, command, timeout, sudo, bash -c). "
        "Allowed forms: `git push -u origin <branch>`, `cd <dir> && git commit -m \"<message>\"`. "
        "For a multi-line message use `git commit -F <file>` or repeated `-m`."
    ),
    "merge": "only the maintainer merges a PR. Report the PR as ready instead.",
    "alias": "a gh alias can hide `gh pr merge`. Run the gh command it stands for directly.",
    "git-config": "a `-c` override of alias.*, push.*, remote.*, branch.* or core.hooksPath can change what the command does. "
                  "Run it without the override.",
    "refspec": "a src:dst push can land one branch's commits on another branch (#900). " + PUSH_YOUR_BRANCH,
    "force": "a fix is a new commit, never a rewrite of pushed history. Commit the fix and push normally.",
    "all": "it pushes or deletes more than your branch (--all, --branches, --prune). " + PUSH_YOUR_BRANCH,
    "push-main": "main changes only through a merged PR. Run `git switch -c <type>/<topic>`, then push that branch.",
    "push-config": "the push configuration (push.default=matching, remote.<name>.mirror, or a forced, matching, "
                   "wildcard or main remote.<name>.push) can update main or force. Push to a remote without it.",
    "upstream-main": "with push.default=upstream this branch would push to its upstream, main. "
                     "Run `git branch --unset-upstream`, then `git push -u origin <that-branch>`.",
    "shared": "this is the shared checkout, the main worktree other agents' worktrees hang off; git commands that "
              "change its HEAD, index, files or refs are refused here. Run it in your own worktree: "
              "`cd <your worktree> && git ...`.",
    "unresolved": "git-guard cannot tell which checkout each git step targets here (a subshell, wrapper, "
                  "variable assignment or directory change it cannot follow), and it may be the shared checkout. "
                  "Run each git command as its own simple command, or join simple steps with &&, ; or newlines.",
    "stash": "every worktree shares one .git, so the stash list is shared with other agents. Commit "
             "work in progress on your own branch instead.",
    "shallow": "every worktree shares one .git, so a fetch or pull that changes the shallow state (--depth, "
               "--deepen, --shallow-*, --unshallow, --update-shallow or an abbreviation) changes it for every "
               "worktree. Fetch without them.",
    "commit-main": "main is protected. Run `git switch -c <type>/<topic>` first, then commit there.",
    "option": "git-guard does not know this option, so it cannot tell what the write does. Drop it or spell it out.",
    "gh-api-write": "a non-GET call on merges, contents, git/refs, rulesets or branches/<b>/protection can merge, "
                    "commit, move a branch or change main's protection outside a PR. Push changes on your branch instead; "
                    "reads of these paths are allowed.",
    "send-pack": "send-pack is `git push` without its checks or the pre-push hook. " + PUSH_YOUR_BRANCH,
    "update-ref-stdin": "git-guard cannot see the refs that --stdin will receive later. Name the ref on the command line.",
    "update-ref-main": "it moves the local main ref. Fast-forward it with `git fetch origin main:main`; anything else "
                       "reaches main through a merged PR.",
    "config-write": "an alias.* entry can hide a write, core.hooksPath turns the hook backstop off, and an editor can "
                    "change either unseen. Run the git command directly, and leave the hooks as configured.",
    "no-verify": "--no-verify skips .githooks/pre-push, the backstop behind this guard. Push without it.",
    "graphql-write": "this mutation can merge a PR, commit, move a branch or change main's protection outside a PR. "
                     "Only the maintainer merges; push changes on your branch instead. Read-only queries are allowed.",
    "query-file": "git-guard cannot read the GraphQL query from that source (a regular file with the query, or JSON "
                  "with a string `query` for --input; never stdin or a device), so it cannot tell what it mutates. "
                  "Pass the query inline with `-f query='…'`.",
}


class Blocked(Exception):
    pass


def block(rule, shown):
    raise Blocked(f"`{shown}` is blocked: {MESSAGES[rule]}")


def has_simple_shape(command, operators="", braced_words=False):
    """True when the only unquoted shell operators are `&&` and any of `operators` (`;`, `|`). With braced_words,
    `{word}` passes too: bash expands braces only around `,` or `..`, so gh's `{owner}` is literal."""
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
        elif braced_words and c == "{" and (braced := BRACED_WORD.match(command, i)):
            i = braced.end()
            continue
        elif (c in "\n;|(){}<>*?[" and c not in operators) or (c == "#" and (i == 0 or command[i - 1] in " \t")):
            return False
        i += 1
    return True  # an unclosed quote fails in shlex below


def simple_command(command, cwd):
    """(argv, cwd) for `[cd <dir> &&] <argv> [2>&1 | 2>/dev/null]`; block any other shape."""
    command = ALLOWED_SUFFIX.sub("", command)
    if not has_simple_shape(command):
        block("simple", command)
    lex = shlex.shlex(command, posix=True, punctuation_chars="&")
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
        cwd = cd_dir(cwd, parts[0][1], command)
    argv = parts[-1]
    if any(set(token) <= {"&"} for token in argv):
        block("simple", command)
    return argv, cwd


def semicolons_for_newlines(command):
    """The command with each unquoted, unescaped newline replaced by `;`, the separator it is to the shell. After
    &&, ||, |, ; or another newline, it continues the command or is a blank line, so it becomes a space."""
    out, quote, escaped = [], None, False
    for c in command:
        if escaped:
            escaped = False
        elif c == "\\" and quote != "'":
            escaped = True
        elif quote:
            quote = None if c == quote else quote
        elif c in "'\"":
            quote = c
        elif c == "\n":
            before = "".join(out).rstrip(" \t")
            c = " " if not before or before.endswith(("&&", "|", ";")) else ";"
        out.append(c)
    return "".join(out)


def shell_steps(command, cwd):
    """[(program, argv, cwd)] for every step of simple steps joined by &&, ||, ;, | or newlines. A `cd <dir>`
    step moves the steps after it only when it always runs and its result is used: first or after `;`, and
    followed by && or `;`. None for any other shape, another cd, pushd or popd."""
    command = semicolons_for_newlines(ALLOWED_SUFFIX.sub("", command))
    if not has_simple_shape(command, operators=";|", braced_words=True):
        return None
    lex = shlex.shlex(command, posix=True, punctuation_chars=";&|")
    lex.whitespace_split = True
    lex.commenters = ""
    steps, operators = [[]], [";"]  # operators[n] is the one before steps[n]
    for token in lex:
        if token in ("&&", "||", ";", "|"):
            steps.append([])
            operators.append(token)
        elif set(token) <= set(";&|"):
            return None
        else:
            steps[-1].append(token)
    operators.append(";")
    found = []
    for n, argv in enumerate(steps):
        program = os.path.basename(argv[0]) if argv else ""
        if not argv:
            if operators[n] != ";" or operators[n + 1] != ";":
                return None  # only `;` and newlines may leave an empty step
        elif argv[0] in ("pushd", "popd"):
            return None  # the directory stack is not followed
        elif argv[0] == "cd":
            if len(argv) != 2 or operators[n] != ";" or operators[n + 1] not in ("&&", ";"):
                return None
            cwd = cd_dir(cwd, argv[1], command)
        else:
            found.append((program, argv, cwd))
    return found


def git_steps(command, cwd):
    """The git and gh steps of shell_steps. None when a step's target cannot be resolved: a variable assignment,
    or a program outside DATA_PROGRAMS whose arguments name git (it may run git itself)."""
    steps = shell_steps(command, cwd)
    found = []
    for program, argv, step_cwd in steps or []:
        if program in ("git", "gh"):
            found.append((program, argv, step_cwd))
        elif ASSIGNMENT.match(argv[0]) or (
                (program not in DATA_PROGRAMS or any(arg.startswith(DATA_PROGRAM_RUNNERS) for arg in argv[1:]))
                and any(GIT_WORD.search(arg) for arg in argv[1:])):
            return None
    return None if steps is None else found


def refuse_unresolved(command, cwd):
    """A git command git_steps cannot resolve runs where the session is only if nothing in it can move the
    target; it refuses when something can, or when the session is in the shared checkout."""
    if DIRECTORY_CHANGE.search(plain_text(command)) or in_shared_checkout(cwd):
        block("unresolved", command)


def in_shared_checkout(cwd, options=()):
    """True when cwd (with git's global options) is the shared checkout; False outside any repository; any other
    lookup failure blocks."""
    if cwd is None or not os.path.isdir(cwd):
        return False
    try:
        return Repo(cwd, list(options), cwd).is_shared_checkout()
    except Blocked as failure:
        if "not a git repository" in str(failure):
            return False
        raise


def cd_dir(base, operand, shown):
    """Directory `cd <operand>` lands in (bash's default -L resolves `..` by text, as normpath does)."""
    searched = os.environ.get("CDPATH") and not operand.startswith("/") and operand.split("/")[0] not in (".", "..")
    if operand.startswith(("-", "~")) or searched:
        raise Blocked(f"`{shown}` is blocked: where `cd {operand}` lands depends on the shell (an option, `~` or "
                      "CDPATH). Use `cd /absolute/path &&` or `cd ./relative/path &&`.")
    if base is None and not operand.startswith("/"):
        return None  # process input: the running shell's directory is unknown
    return os.path.normpath(os.path.join(base or "/", operand))


class Repo:
    """Read-only git lookups in the directory the command runs in; a failed lookup blocks."""

    def __init__(self, cwd, options, shown):
        if not cwd or not os.path.isdir(cwd):
            raise Blocked(f"`{shown}` is blocked: git-guard cannot find the directory it runs in. "
                          "Run it from an existing directory, or with `cd <absolute dir> &&`.")
        self.cwd, self.options, self.shown = cwd, options, shown

    def git(self, *args):
        return subprocess.run(["git", *self.options, *args], cwd=self.cwd, capture_output=True, text=True)

    def lookup_failed(self, what, result):
        return Blocked(f"`{self.shown}` is blocked: git-guard could not read {what} in {self.cwd} "
                       f"({result.stderr.strip() or 'exit ' + str(result.returncode)}). Fix that, then retry.")

    def branch(self):
        """Checked-out branch from HEAD's full ref (`--short` can say heads/main), or None when detached."""
        result = self.git("symbolic-ref", "--quiet", "HEAD")
        if result.returncode not in (0, 1):
            raise self.lookup_failed("the current branch", result)
        return result.stdout.strip().removeprefix("refs/heads/") or None

    def is_shared_checkout(self):
        """True in a main worktree that has linked worktrees, the checkout other agents' worktrees share."""
        dirs = self.git("rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir")
        listing = self.git("worktree", "list", "--porcelain")
        for result in (dirs, listing):
            if result.returncode != 0:
                raise self.lookup_failed("the worktree layout", result)
        git_dir, common = dirs.stdout.splitlines()
        return git_dir == common and sum(line.startswith("worktree ") for line in listing.stdout.splitlines()) > 1

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


def long_option(arg, shown, options=PUSH_LONG_OPTIONS):
    """Full name of a long option from `options`, resolving git's unambiguous prefixes."""
    name = arg[2:].split("=", 1)[0]
    negated = name.startswith("no-")
    base = name[3:] if negated else name
    matches = [base] if base in options else [o for o in options if o.startswith(base)]
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
            if name == "no-verify":
                block("no-verify", shown)
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

    remotes = {key[len("remote."):].rsplit(".", 1)[0] for key in config if key.startswith("remote.") and key.count(".") > 1}
    remote = positional[0] if positional else (
        setting(f"branch.{branch}.pushremote") or setting("remote.pushdefault") or setting(f"branch.{branch}.remote")
        or (remotes.pop() if len(remotes) == 1 else "origin"))  # git falls back to a sole remote before origin
    push_default = (setting("push.default") or "simple").lower()
    mirror = config.get(f"remote.{remote}.mirror")
    if push_default == "matching" or (mirror and (mirror[-1] is None or mirror[-1].lower() not in GIT_FALSE)):
        block("push-config", shown)
    for configured in config.get(f"remote.{remote}.push") or []:
        destination = (configured or "").split(":")[-1]
        if not destination or configured.startswith("+") or "*" in configured or destination in MAIN_REFS:
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


BRANCH_LISTING = {"list", "all", "remotes", "show-current", "merged", "no-merged", "contains", "no-contains",
                  "points-at", "verbose", "sort", "format", "column", "no-column", "color", "no-color",
                  "ignore-case", "abbrev", "no-abbrev", "omit-empty", "delete"}
BRANCH_VALUES = {"--format", "--sort", "--points-at"}
BRANCH_TAKES_NAMES = {"list", "all", "remotes", "merged", "no-merged", "contains", "no-contains", "points-at", "delete"}


def writes_anyway(command, args):
    """A SHARED_CHECKOUT_SAFE subcommand in a form that writes: --output=<file>, fetch --update-head-ok, or
    branch other than listing and deleting (create, -m, -c, -f, --set-upstream-to)."""
    if command == "branch":  # drop the separate values of --format, --sort and --points-at before classifying
        args = [arg for n, arg in enumerate(args) if not (n and args[n - 1] in BRANCH_VALUES)]
    longs = {arg[2:].split("=", 1)[0] for arg in args if arg.startswith("--") and len(arg) > 3}
    shorts = set("".join(arg[1:] for arg in args if arg.startswith("-") and not arg.startswith("--")))
    if any("output".startswith(name) for name in longs):
        return True
    if command == "fetch":
        return "u" in shorts or any("update-head-ok".startswith(name) for name in longs)
    if command == "branch":
        names = any(not arg.startswith("-") for arg in args)
        listing = shorts <= set("dDlarvi") and longs <= BRANCH_LISTING
        return not listing or (names and not (shorts & set("dDlar") or longs & BRANCH_TAKES_NAMES))
    return False


def config_writes(args, shown):
    """`git config` in edit mode (an editor, possibly `-c core.editor=sed`, can change any key unseen), or one that
    sets, adds or unsets an alias.* or core.hooksPath key in any scope. A read (--get*, --list, --help, the
    get/list subcommands, or a lone key) is not a write. One walk over argv: a long option resolves from its
    prefix (an unknown one blocks), an option that takes a value consumes the next token whatever it looks
    like, and nothing after `--` is an option."""
    edit = read = write = False
    positional = []
    i = 0
    while i < len(args):
        arg = args[i]
        i += 1
        if arg == "--":
            positional += args[i:]
            break
        if arg in ("-h", "--help"):
            return False
        if arg.startswith("--"):
            name = long_option(arg, shown, CONFIG_LONG_OPTIONS)
            if name in CONFIG_VALUES and "=" not in arg:
                i += 1
            edit |= name == "edit"
            read |= name in CONFIG_READS
            write |= name in CONFIG_WRITES
        elif arg.startswith("-") and len(arg) > 1:
            for j, flag in enumerate(arg[1:], start=1):
                if flag not in "elfz":
                    block("option", shown)
                edit |= flag == "e"
                read |= flag == "l"
                if flag == "f":  # its file is the rest of the token, or else the next one
                    i += 0 if arg[j + 1 :] else 1
                    break
        else:
            positional.append(arg)
    if edit or positional[:1] == ["edit"]:
        return True
    if not any(CONFIG_GUARDED_KEY.match(arg) for arg in positional):
        return False
    if read or positional[:1] in (["get"], ["list"]):
        return False
    return len(positional) > 1 or write


INIT_VALUES = {"-b", "--initial-branch", "--separate-git-dir", "--template", "--object-format", "--ref-format"}


def init_target(args, cwd, options):
    """The directory `git init` writes: cwd, moved by -C, then by its directory operand."""
    for flag, value in zip(options, options[1:]):
        if flag == "-C":
            cwd = os.path.join(cwd or "/", value)
    directory, skip = None, False
    for arg in args:
        if skip:
            skip = False
        elif arg in INIT_VALUES:
            skip = True
        elif not arg.startswith("-"):
            directory = arg
    return os.path.normpath(os.path.join(cwd, directory)) if cwd and directory else cwd


def changes_shallow_state(arg):
    name = arg[2:].split("=", 1)[0] if arg.startswith("--") else ""
    return len(name) >= 2 and any(option.startswith(name) for option in SHALLOW_OPTIONS)


def check_git(args, cwd, shown):
    options = []
    i = 0
    while i < len(args) and args[i].startswith("-"):
        arg = args[i]
        name = arg.split("=", 1)[0]
        if arg in GIT_GLOBAL_VALUES and i + 1 < len(args):
            value = args[i + 1]
            if arg == "-c" and re.match(r"(alias|push|remote|branch)\.|core\.hookspath", value, re.I):
                block("git-config", shown)
            options += [arg, value]  # -C included: git resolves the path itself, symlinks and all
            i += 2
        elif arg in GIT_GLOBAL_FLAGS or (name in ("--git-dir", "--work-tree") and "=" in arg):
            options.append(arg)
            i += 1
        else:
            block("option", shown)
    if i >= len(args):
        return
    command, rest, repo = args[i], args[i + 1 :], None
    if command == "clone":
        return  # a clone has its own .git, shallow or not; the current checkout plays no part
    if command == "init":  # creates its target, so a missing repository there is not the shared checkout
        # with --git-dir or --work-tree, git resolves the repository init writes; otherwise init_target does
        overridden = any(option.startswith(("--git-dir", "--work-tree")) for option in options)
        target = in_shared_checkout(cwd, options) if overridden else in_shared_checkout(init_target(rest, cwd, options))
        if os.environ.get("GIT_GUARD_ALLOW_SHARED") != "1" and target:
            block("shared", shown)
        return
    if command == "send-pack":
        block("send-pack", shown)
    if command == "config" and config_writes(rest, shown):
        block("config-write", shown)
    if os.environ.get("GIT_GUARD_ALLOW_SHARED") != "1":
        if command == "stash":
            block("stash", shown)
        if command in ("fetch", "pull") and any(changes_shallow_state(arg) for arg in rest):
            block("shallow", shown)
        if command not in SHARED_CHECKOUT_SAFE or writes_anyway(command, rest):
            repo = Repo(cwd, options, shown)
            if repo.is_shared_checkout():
                block("shared", shown)
    if command not in ("commit", "push", "update-ref"):
        return
    repo = repo or Repo(cwd, options, shown)
    if command == "push":
        check_push(rest, repo)
    elif command == "update-ref":
        if any(len(arg) >= 3 and "--stdin".startswith(arg) for arg in rest):
            block("update-ref-stdin", shown)
        operands, i = [], 0
        while i < len(rest):
            if rest[i] == "-m":
                i += 1  # its message, even one spelled `-m`
            elif not rest[i].startswith("-"):
                operands.append(rest[i])
            i += 1
        destination = operands[0] if operands else None
        if (repo.branch() if destination in ("HEAD", "@") else destination) in MAIN_REFS:
            block("update-ref-main", shown)
    elif repo.branch() == MAIN:
        block("commit-main", shown)


def query_text(name, value, cwd, shown):
    """The query a body option sends to graphql: the value, the file behind `-F key=@file`, or the string `query`
    of the JSON file behind `--input file`. Anything the guard cannot read as that blocks: a missing file, a
    device, FIFO or stdin spelling, a path relative to an unknown directory, or a body that is not JSON with a
    string query."""
    path = value.split("=", 1)[1][1:] if name in ("-F", "--field") and "=@" in value else value if name == "--input" else None
    if path is None:
        return value
    if path == "-" or path.startswith(("/dev/", "/proc/")) or (cwd is None and not os.path.isabs(path)):
        block("query-file", shown)
    full = os.path.join(cwd or "/", path)
    try:
        if not stat.S_ISREG(os.stat(full).st_mode):  # stat first: opening a FIFO would hang
            raise OSError
        with open(full) as f:
            text = f.read()
    except (OSError, UnicodeDecodeError):
        block("query-file", shown)
    if name == "--input":  # the JSON body gh sends; the server reads its decoded `query`
        try:
            text = json.loads(text)["query"]
        except (ValueError, KeyError, TypeError):
            block("query-file", shown)
        if not isinstance(text, str):
            block("query-file", shown)
    return text


def gh_api_request(args, shown):
    """(method, path, body fields) of a `gh api` call; an option this guard does not know blocks."""
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
            fields.append((name, value))
    method = method or ("POST" if fields else "GET")
    path = urllib.parse.unquote(urllib.parse.urlparse(positional[0]).path) if positional else ""  # URL, query or not
    return method, path, fields


def check_gh_api(args, shown, cwd):
    method, path, fields = gh_api_request(args, shown)
    if method != "GET" and re.search(r"(^|/)pulls/[^/]+/merge/?$", path):
        block("merge", shown)
    if method != "GET" and GH_API_PROTECTED.search(path):
        block("gh-api-write", shown)
    if re.search(r"(^|/)graphql/?$", path):
        if any(re.search(GRAPHQL_WRITES, query_text(name, value, cwd, shown)) for name, value in fields):
            block("graphql-write", shown)


def gh_words(args):
    """(index, word) of the first two non-flag arguments, past -R/--repo and its value."""
    words = []
    i = 0
    while i < len(args) and len(words) < 2:
        if args[i] in ("-R", "--repo"):
            i += 2
            continue
        if not args[i].startswith("-"):
            words.append((i, args[i]))
        i += 1
    return words


def is_gh_api_read(args):
    """A `gh api` GET on a REST path: a chain of these runs even when a path names a write route. An unknown
    option, a missing path or a graphql call is not a read."""
    words = gh_words(args)
    if [word for _, word in words][:1] != ["api"]:
        return False
    try:
        method, path, _ = gh_api_request(args[words[0][0] + 1 :], "")
    except Blocked:
        return False
    return method == "GET" and bool(path) and not re.search(r"(^|/)graphql/?$", path)


def is_read_chain(steps):
    """Every step is a `gh api` GET on a REST path or a READ_FILTERS program: the one chain that mentions a
    write route and still runs without the simple-command rule. Any other step, or an unparsed chain, is not."""
    return bool(steps) and all(
        (program == "gh" and is_gh_api_read(argv[1:]))
        or program in READ_FILTERS
        for program, argv, _ in steps)


def check_gh(args, shown, cwd):
    words = gh_words(args)
    names = [word for _, word in words]
    if names == ["pr", "merge"]:
        block("merge", shown)
    if names[:1] == ["alias"] and names[1:] != ["list"]:
        block("alias", shown)
    if names[:1] == ["api"]:
        check_gh_api(args[words[0][0] + 1 :], shown, cwd)


def plain_text(command):
    """The command with quotes, backslashes and %-escapes undone, as `pu""sh` and `merg%65` would be."""
    return urllib.parse.unquote(re.sub(r"[\"'\\]", "", command.replace("\\\n", "")))


def mentions_write(command):
    """Text check before parsing. An expansion beside git, gh or a write verb counts too, since it can spell
    the rest (`/usr/bin/gi? push`)."""
    plain = plain_text(command)
    return (MENTIONS_WRITE.search(plain) or CHANGES_CHECKOUT.search(plain)
            or (SHALLOW.search(plain) and GIT_WORD.search(plain))
            or (EXPANSIONS.search(command) and MENTIONS_TOOL_OR_VERB.search(plain)))


def check(command, cwd, unchecked=None):
    if not mentions_write(command):
        # No write in the text, but in the shared checkout every git step still meets the allow-list.
        if unchecked or not GIT_WORD.search(plain_text(command)):
            return
        steps = git_steps(command, cwd)
        if steps is None:
            refuse_unresolved(command, cwd)
        for program, argv, step_cwd in steps or []:
            if program == "gh":
                check_gh(argv[1:], " ".join(argv), step_cwd)
            else:
                check_git(argv[1:], step_cwd, " ".join(argv))
        return
    if unchecked:
        raise Blocked(f"this terminal call mentions, or could spell, a git write or PR merge, and {unchecked}. "
                      "Run the git command on the local backend with an absolute `workdir`.")
    if is_read_chain(shell_steps(command, cwd)):
        return  # reads of write routes (rulesets, protection, ...) may be piped or chained
    argv, cwd = simple_command(command, cwd)
    program = os.path.basename(argv[0])
    shown = " ".join(argv)
    if program == "git":
        check_git(argv[1:], cwd, shown)
    elif program == "gh":
        check_gh(argv[1:], shown, cwd)
    elif program not in INERT_PROGRAMS:
        block("simple", shown)


def read_payload(text):
    """(command, cwd, unchecked) for check(), or None for a tool that runs nothing.

    cwd is None for process input; unchecked is the plugin's reason it cannot name the repository."""
    payload = json.loads(text)
    tool = payload.get("tool_name", "Bash")
    if not isinstance(tool, str) or not tool:
        raise ValueError("tool_name is not a non-empty string")
    if tool in REFUSED_TOOLS:
        raise Blocked(f"the {tool} tool is blocked in this repository: {REFUSED_TOOLS[tool]}")
    if tool not in SHELL_TOOLS and tool != "process_manage":
        return None
    tool_input = payload.get("tool_input")
    if not isinstance(tool_input, dict):
        raise ValueError("tool_input is not an object")
    if tool == "process_manage":  # only write and submit carry data
        command = tool_input.get("data", "")  # Hermes sends Enter alone when data is absent
        if not isinstance(command, str):
            raise ValueError("tool_input.data is not a string")
        return command, None, None
    command = tool_input.get("command")
    cwd = payload.get("cwd")
    if not isinstance(command, str) or not isinstance(cwd, str) or not cwd:
        raise ValueError("tool_input.command or cwd is missing, empty or not a string")
    workdir = tool_input.get("workdir") if tool == "terminal" else None
    if workdir is not None and (not isinstance(workdir, str) or not workdir):
        raise ValueError("tool_input.workdir is empty or not a string")
    unchecked = payload.get("unchecked") if tool == "terminal" else None
    return command, os.path.join(cwd, workdir) if workdir else cwd, unchecked


def main():
    try:
        request = read_payload(sys.stdin.read())
    except Blocked as reason:
        print(f"git-guard: {reason}", file=sys.stderr)
        return 2
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
