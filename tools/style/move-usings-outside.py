#!/usr/bin/env python3
"""One-shot conversion for #985: move `using` directives from inside a
file-scoped namespace to above it, which is the C# default placement.

Committed so a reviewer can rerun this and diff the result, rather than
reading 590 mechanical file changes by hand.

A file with usings in both places is not ambiguous: the inside group joins the
outside one, in original order. Skipped deliberately, never guessed at: any
file with no file-scoped namespace, which covers the two multi-namespace guard
fixtures and every EF migration, since those already place usings outside.

`dotnet format style <project> --diagnostics IDE0065 IDE0161 --severity warn`
does the same job one project at a time and is the supported route for new
code. This exists to make one 590-file diff re-derivable in a single command.

Usage: python3 tools/style/move-usings-outside.py $(git ls-files '*.cs')
"""
import pathlib
import re
import sys

FILE_SCOPED = re.compile(r'^namespace [^{]*;\s*$')
USING = re.compile(r'^using [A-Za-z@]')


def collapse_seam(lines, at):
    """Collapse the run of blanks just after `at` to a single blank line.

    Scoped to the seam the move opens up. A global pass would also delete
    pre-existing double blanks elsewhere, which is churn this change has no
    business making.
    """
    k = at + 1
    while k < len(lines) and not lines[k].strip():
        k += 1
    if k > at + 2:
        return lines[:at + 1] + [''] + lines[k:]
    return lines


def convert(path):
    lines = pathlib.Path(path).read_text(encoding='utf-8').split('\n')
    ns = next((i for i, l in enumerate(lines) if FILE_SCOPED.match(l)), None)
    if ns is None:
        return False
    inside = [i for i, l in enumerate(lines[ns + 1:], ns + 1) if USING.match(l)]
    if not inside:
        return False
    # A comment touching a using with no blank line between them annotates that
    # using; it has to travel with it or it ends up explaining nothing.
    for i in list(inside):
        j = i - 1
        while j > ns and lines[j].lstrip().startswith('//') and j not in inside:
            inside.append(j)
            j -= 1
    inside.sort()
    outside = [i for i, l in enumerate(lines[:ns]) if USING.match(l)]
    moved = [lines[i] for i in inside]
    rest = [l for i, l in enumerate(lines) if i not in set(inside)]
    if outside:
        at = next(i for i, l in enumerate(rest) if FILE_SCOPED.match(l))
        last = max(i for i, l in enumerate(rest) if USING.match(l) and i < at)
        rest = rest[:last + 1] + moved + rest[last + 1:]
        at = next(i for i, l in enumerate(rest) if FILE_SCOPED.match(l))
        pathlib.Path(path).write_text(
            '\n'.join(collapse_seam(rest, at)), encoding='utf-8')
        return True
    at = next(i for i, l in enumerate(rest) if FILE_SCOPED.match(l))
    head = rest[:at]
    while head and not head[-1].strip():
        head.pop()
    tail = collapse_seam(rest[at:], 0)
    out = head + ([''] if head else []) + moved + [''] + tail
    while len(out) > 1 and not out[0].strip():
        out.pop(0)
    pathlib.Path(path).write_text('\n'.join(out), encoding='utf-8')
    return True


if __name__ == '__main__':
    print(sum(convert(f) for f in sys.argv[1:]), 'files converted')
