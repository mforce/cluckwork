#!/usr/bin/env python3
"""Package-by-module codemod (#1087). The compiler is the check; re-run it on a rebase instead of merging by hand.

    move.py split <table.json>    extract the listed declarations into <Type>.cs beside their file, same namespace
    move.py move  <table.json>    git mv each file, set its namespace to its new folder, rewrite references
    move.py prune <table.json> <base.log> <head.log>
                                  delete the using lines IDE0005 flags in head but not in base (see `ide0005`)
    move.py ide0005 <out.log>     build the solution with IDE0005 reported, for prune
    move.py marks                 delete the [ModuleContract] marks of types in a Contracts folder

The table is finite and explicit, one entry per file (after split, per type):

    {"split": {"src/.../IReportQueries.cs": ["ProductionDay", "GradeTotal"]},
     "move":  {"src/.../ProductionDay.cs": "src/Cluckwork.Application/Modules/Insights/Contracts/ProductionDay.cs"},
     "replace": {"src/.../Insights.cs": [["old literal", "new literal"]]}}

`move` rewrites, in every .cs file under src/, tests/ and tools/: fully and partially qualified names of each moved
type (code, strings and comments alike, so registry rows follow), `using` directives (adds the new namespace where a
moved type or one of its extension methods is named, adds the namespaces a moved file lost as ancestors, drops a using whose namespace emptied), and
project-relative paths of moved files, also in Markdown outside docs/decisions and docs/plans. `replace` covers what
cannot be derived, such as a rules file's namespace roots. Run `split` and `move` from the repository root.
"""
import json
import os
import re
import subprocess
import sys

CODE_ROOTS = ("src", "tests", "tools")
HISTORY = ("docs/decisions/", "docs/plans/")
TYPE_KEYWORD = re.compile(
    r"\b(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?|delegate\s+[\w<>\[\],.?\s]+?)\s+@?(\w+)")
NAMESPACE = re.compile(r"^namespace\s+([\w.]+)\s*;", re.M)
# A caller of an extension method names the method, never its class.
EXTENSION = re.compile(r"\bstatic\b[^;{}=]*?\b(\w+)\s*(?:<[^<>()]*>)?\s*\(\s*this\b")
USING = re.compile(r"^(global\s+)?using\s+([\w.]+)\s*;[ \t]*\r?\n", re.M)


def mask(text):
    """The text with comments, strings and char literals blanked (newlines kept), so offsets still line up."""
    out, i, n = list(text), 0, len(text)

    def blank(a, b):
        for k in range(a, b):
            if out[k] not in "\r\n":
                out[k] = " "

    def string_end(i):
        # i is at the first quote; prefix holds any $ and @ before it.
        j = i
        while j > 0 and text[j - 1] in "$@":
            j -= 1
        prefix = text[j:i]
        dollars = prefix.count("$")
        quotes = re.match(r'"*', text[i:i + 64]).end()
        if quotes >= 3:
            end = text.index('"' * quotes, i + quotes)
            return end + quotes
        k = i + 1
        while k < n:
            c = text[k]
            if c == "\\" and "@" not in prefix:
                k += 2
                continue
            if c == '"':
                if "@" in prefix and text[k:k + 2] == '""':
                    k += 2
                    continue
                return k + 1
            if c == "{" and dollars:
                if text[k:k + 2] == "{{":
                    k += 2
                    continue
                depth, k = 1, k + 1
                while depth and k < n:
                    if text[k] == '"':
                        k = string_end(k)
                        continue
                    depth += {"{": 1, "}": -1}.get(text[k], 0)
                    k += 1
                continue
            k += 1
        return k

    while i < n:
        c = text[i]
        if text.startswith("//", i):
            end = text.find("\n", i)
            end = n if end < 0 else end
        elif text.startswith("/*", i):
            end = text.index("*/", i) + 2
        elif c == '"':
            start = i
            while start > 0 and text[start - 1] in "$@":
                start -= 1
            end = string_end(i)
            i = start
        elif c == "'":
            end = i + 1
            while text[end] != "'":
                end += 2 if text[end] == "\\" else 1
            end += 1
        else:
            i += 1
            continue
        blank(i, end)
        i = end
    return "".join(out)


def declarations(text):
    """(name, start, end) of each top-level declaration after a file-scoped namespace. start includes its leading
    comments and attributes, everything since the previous declaration ended. Empty without a file-scoped namespace."""
    code = mask(text)
    ns = NAMESPACE.search(code)
    if not ns:
        return []
    result, depth, parens, start, i = [], 0, 0, ns.end(), ns.end()
    while i < len(code):
        c = code[i]
        depth += {"{": 1, "}": -1}.get(c, 0)
        parens += {"(": 1, ")": -1, "[": 1, "]": -1}.get(c, 0)
        if depth == 0 and parens == 0 and c in "};" and code[start:i].strip():
            head = TYPE_KEYWORD.search(code, start, i + 1)
            if head:
                lead = re.compile(r"[\r\n]*").match(text, start).end()
                result.append((head.group(1), lead, i + 1))
                start = i + 1
        i += 1
    return result


def read(path):
    with open(path, "rb") as f:
        raw = f.read()
    return raw.decode("utf-8-sig"), raw.startswith(b"\xef\xbb\xbf")


def write(path, text, bom):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8-sig" if bom else "utf-8", newline="") as f:
        f.write(text)


def tidy(text):
    return re.sub(r"(\r?\n){3,}", lambda m: m.group(1) * 2, text)


def split(table):
    for path, names in table.get("split", {}).items():
        text, bom = read(path)
        nl = "\r\n" if "\r\n" in text else "\n"
        found = {name: (a, b) for name, a, b in declarations(text)}
        if not found:
            raise SystemExit(f"{path}: no file-scoped namespace")
        missing = [n for n in names if n not in found]
        if missing:
            raise SystemExit(f"{path}: no top-level declaration {missing}")
        header = "".join(m.group(0) for m in USING.finditer(text[:NAMESPACE.search(text).start()]))
        namespace = NAMESPACE.search(text).group(0)
        for name in names:
            a, b = found[name]
            target = os.path.join(os.path.dirname(path), name + ".cs")
            if os.path.exists(target):
                raise SystemExit(f"{target} exists")
            body = (header + nl if header else "") + namespace + nl + nl + text[a:b] + nl
            write(target, body, bom)
        for name in sorted(names, key=lambda n: -found[n][0]):
            a, b = found[name]
            text = text[:a] + text[b:]
        write(path, tidy(text), bom)
        print(f"split {path}: {', '.join(names)}")


def code_files():
    for root in CODE_ROOTS:
        for dirpath, dirs, files in os.walk(root):
            dirs[:] = [d for d in dirs if d not in ("bin", "obj", "node_modules")]
            for f in files:
                if f.endswith(".cs"):
                    yield os.path.join(dirpath, f)


def namespace_of(text):
    m = NAMESPACE.search(mask(text))
    return m.group(1) if m else None


def folder_namespace(path):
    parts = path.split("/")
    return ".".join(parts[1:-1])  # src/Cluckwork.X/A/B/F.cs -> Cluckwork.X.A.B


def index():
    """namespace -> top-level type names declared in it under src/."""
    types = {}
    for path in code_files():
        if not path.startswith("src/"):
            continue
        text, _ = read(path)
        ns = namespace_of(text)
        if ns:
            types.setdefault(ns, set()).update(name for name, _, _ in declarations(text))
    return types


def names(code, candidates):
    return {m.group(0) for m in re.finditer(r"\b\w+\b", code)} & candidates


def add_using(text, ns):
    if re.search(rf"^using\s+{re.escape(ns)}\s*;", text, re.M):
        return text
    nl = "\r\n" if "\r\n" in text else "\n"
    usings = [m for m in USING.finditer(text) if not m.group(1)]
    line = f"using {ns};{nl}"
    for m in usings:
        if m.group(2) > ns:
            return text[:m.start()] + line + text[m.start():]
    if usings:
        return text[:usings[-1].end()] + line + text[usings[-1].end():]
    ns_line = NAMESPACE.search(text)
    at = ns_line.start() if ns_line else 0
    return text[:at] + line + nl + text[at:]


def move(table):
    before = index()
    moves = []
    for src, dst in table.get("move", {}).items():
        text, _ = read(src)
        old, new = namespace_of(text), folder_namespace(dst)
        moved = {name for name, _, _ in declarations(text)}
        moves.append((src, dst, old, new, moved, set(EXTENSION.findall(mask(text)))))
    remaining = {ns: set(t) for ns, t in before.items()}
    for _, _, old, _, moved, _ in moves:
        remaining[old] -= moved
    emptied = {ns for ns, t in remaining.items() if not t}

    for src, dst, old, new, _, _ in moves:
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        subprocess.run(["git", "mv", src, dst], check=True)
        text, bom = read(dst)
        text = re.sub(rf"^namespace\s+{re.escape(old)}\s*;", f"namespace {new};", text, count=1, flags=re.M)
        # The ancestors of the old namespace that are not ancestors of the new one stop being in scope.
        code = mask(text)
        parts = old.split(".")
        for k in range(len(parts), 0, -1):
            ancestor = ".".join(parts[:k])
            if new == ancestor or new.startswith(ancestor + "."):
                break
            if names(code, remaining.get(ancestor, set())):
                text = add_using(text, ancestor)
        write(dst, text, bom)
        print(f"move {src} -> {dst} ({old} -> {new})")

    # Full names everywhere, so registry strings follow; partial ones (Features.Reports.X) only in code, because a
    # string or comment holding one is an example, such as a log category, not a reference.
    qualified = {}
    for _, _, old, new, moved, _ in moves:
        parts = old.split(".")
        for name in moved:
            for k in range(len(parts)):
                qualified[".".join(parts[k:]) + "." + name] = (f"{new}.{name}", k == 0)
    rx = re.compile(r"(?<![\w.])(?:global::)?(" + "|".join(
        re.escape(q) for q in sorted(qualified, key=len, reverse=True)) + r")\b") if qualified else None

    def qualify(match, code):
        name, is_full = qualified[match.group(1)]
        return name if is_full or code[match.start()] != " " else match.group(0)

    paths = [(s.split("/", 1)[1], d.split("/", 1)[1]) for s, d, *_ in moves]
    origin = {dst: old for _, dst, old, *_ in moves}

    for path in code_files():
        text, bom = read(path)
        original = text
        if rx:
            code = mask(text)
            text = rx.sub(lambda m: qualify(m, code), text)
        for a, b in paths:
            text = text.replace(a, b)
        own = namespace_of(text)
        was = origin.get(path, own)
        code = mask(text)
        visible = {m.group(2) for m in USING.finditer(text)}
        for _, _, old, new, moved, extensions in moves:
            if own and (own == new or own.startswith(new + ".")):
                continue
            sees_old = old in visible or (was and (was == old or was.startswith(old + ".")))
            if sees_old and names(code, moved | extensions):
                text = add_using(text, new)
        for m in reversed(list(USING.finditer(text))):
            if m.group(2) in emptied:
                text = text[:m.start()] + text[m.end():]
        if text != original:
            write(path, text, bom)

    for path in subprocess.run(["git", "ls-files", "*.md"], capture_output=True, text=True, check=True).stdout.split():
        if path.startswith(HISTORY) or path.startswith("graphify-out/"):
            continue
        text, bom = read(path)
        updated = text
        for a, b in paths:
            updated = updated.replace(a, b)
        if updated != text:
            write(path, updated, bom)

    for path, pairs in table.get("replace", {}).items():
        text, bom = read(path)
        for a, b in pairs:
            if a not in text:
                raise SystemExit(f"{path}: {a!r} not found")
            text = text.replace(a, b)
        write(path, text, bom)


def ide0005(out):
    """Builds the solution with IDE0005 reported, writing one `path:using line` per unnecessary using to out. IDE0005
    needs documentation files, and reports one diagnostic per run of usings, so the runs are read from SARIF."""
    with open(".editorconfig", encoding="utf-8") as f:
        editorconfig = f.read()
    props = os.path.abspath("obj-ide0005.props")
    with open(props, "w", encoding="utf-8") as f:
        f.write("<Project><PropertyGroup><ErrorLog>$(MSBuildProjectDirectory)/obj/ide0005.sarif,version=2.1"
                "</ErrorLog></PropertyGroup></Project>\n")
    try:
        with open(".editorconfig", "a", encoding="utf-8") as f:
            f.write("\n[*.cs]\ndotnet_diagnostic.IDE0005.severity = warning\n")
        subprocess.run(
            ["dotnet", "build", "Cluckwork.sln", "--no-incremental", "-p:GenerateDocumentationFile=true",
             "-p:NoWarn=CS1591%3BCS1573%3BCS1587%3BCS1574%3BCS1734%3BCS1572", "-p:TreatWarningsAsErrors=false",
             f"-p:CustomAfterMicrosoftCommonProps={props}", "-v", "q"], capture_output=True, check=True)
    finally:
        with open(".editorconfig", "w", encoding="utf-8") as f:
            f.write(editorconfig)
        os.remove(props)
    lines = set()
    for root in CODE_ROOTS:
        for sarif in subprocess.run(["find", root, "-path", "*/obj/ide0005.sarif"], capture_output=True, text=True,
                                    check=True).stdout.split():
            with open(sarif, encoding="utf-8") as f:
                runs = json.load(f)["runs"]
            os.remove(sarif)
            for result in (r for run in runs for r in run.get("results", []) if r["ruleId"] == "IDE0005"):
                location = result["locations"][0]["physicalLocation"]
                path = os.path.relpath(location["artifactLocation"]["uri"].removeprefix("file://"))
                with open(path, encoding="utf-8-sig") as f:
                    source = f.read().splitlines()
                region = location["region"]
                for number in range(region["startLine"], region.get("endLine", region["startLine"]) + 1):
                    if source[number - 1].strip().startswith(("using ", "global using ")):
                        lines.add(f"{path}:{source[number - 1].strip()}")
    with open(out, "w", encoding="utf-8") as f:
        f.write("".join(entry + "\n" for entry in sorted(lines)))
    print(f"{len(lines)} unnecessary usings -> {out}")


def prune(table, base, head):
    """Drops the using lines flagged in head but not in base, reading a moved file's base entries at its new path."""
    moved = {src: dst for src, dst in table.get("move", {}).items() if not os.path.exists(src)}
    with open(base, encoding="utf-8") as f:
        old = {moved.get(path, path) + ":" + line
               for path, line in (e.split(":", 1) for e in f.read().split("\n") if e)}
    with open(head, encoding="utf-8") as f:
        new = [entry for entry in f.read().split("\n") if entry and entry not in old]
    for entry in new:
        path, line = entry.split(":", 1)
        text, bom = read(path)
        text = re.sub(rf"^[ \t]*{re.escape(line)}[ \t]*\r?\n", "", text, count=1, flags=re.M)
        write(path, text.lstrip("\r\n"), bom)
        print(f"prune {path}: {line}")


def marks():
    """Deletes the [ModuleContract] marks of types now in a Contracts folder; the ledger rejects a type with both."""
    for path in code_files():
        if path.startswith("src/") and "/Modules/" in path and "/Contracts/" in path:
            text, bom = read(path)
            updated = re.sub(r"^[ \t]*\[ModuleContract\(\"\w+\"\)\][ \t]*\r?\n", "", text, flags=re.M)
            if updated != text:
                write(path, updated, bom)
                print(f"marks {path}")


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def main(argv):
    if len(argv) == 2 and argv[0] in ("split", "move"):
        (split if argv[0] == "split" else move)(load(argv[1]))
    elif argv == ["marks"]:
        marks()
    elif len(argv) == 2 and argv[0] == "ide0005":
        ide0005(argv[1])
    elif len(argv) == 4 and argv[0] == "prune":
        prune(load(argv[1]), argv[2], argv[3])
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
