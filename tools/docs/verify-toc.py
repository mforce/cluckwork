#!/usr/bin/env python3
"""Check a generated ToC's anchors against GitHub's OWN renderer.

    python3 tools/docs/verify-toc.py specs/product/specs.md

`tools/docs/toc.py` reproduces GitHub's heading-anchor algorithm from its
documented rules. Reproducing an algorithm is a claim, and this script is what
turns it into a check: it POSTs the file to GitHub's `/markdown` endpoint, reads
the anchor ids GitHub actually emitted, and compares them to the links the ToC
contains. A mismatch means a contents entry that scrolls nowhere.

Needs `gh` authenticated and network access, which is exactly why it is a
separate script from the generator and not part of it — regenerating a ToC must
work offline.
"""
from __future__ import annotations

import json
import pathlib
import re
import subprocess
import sys

TOC_LINK = re.compile(r"^\s*-\s+\*?\*?\[[^\]]*\]\(#([^)]+)\)")
RENDERED_ANCHOR = re.compile(r'<a[^>]*\bid="user-content-([^"]+)"')
BEGIN = "<!-- toc:begin"
END = "<!-- toc:end -->"


def main() -> int:
    if len(sys.argv) != 2:
        sys.exit("usage: verify-toc.py <markdown-file>")
    path = pathlib.Path(sys.argv[1])
    lines = path.read_text().splitlines()

    begin = next(i for i, line in enumerate(lines) if line.strip().startswith(BEGIN))
    end = next(i for i, line in enumerate(lines) if line.strip() == END)
    toc_anchors = [m.group(1) for line in lines[begin:end] if (m := TOC_LINK.match(line))]
    if not toc_anchors:
        sys.exit(f"{path}: no ToC links found between the markers — nothing to verify")

    rendered = subprocess.run(
        ["gh", "api", "-X", "POST", "/markdown", "--input", "-"],
        input=json.dumps({"text": path.read_text(), "mode": "markdown"}),
        capture_output=True,
        text=True,
        check=False,
    )
    if rendered.returncode != 0:
        sys.exit(f"gh api /markdown failed:\n{rendered.stderr.strip()}")

    real = set(RENDERED_ANCHOR.findall(rendered.stdout))
    if not real:
        sys.exit("GitHub returned no anchor ids — the request or the parse is wrong, not the ToC")

    broken = [a for a in toc_anchors if a not in real]
    duplicates = [a for a in set(toc_anchors) if toc_anchors.count(a) > 1]

    print(f"{path}: {len(toc_anchors)} ToC links, {len(real)} anchors in GitHub's own render")
    if duplicates:
        print(f"DUPLICATE ToC links ({len(duplicates)}): {sorted(duplicates)}")
    if broken:
        print(f"BROKEN ToC links ({len(broken)}):")
        for anchor in broken:
            print(f"  #{anchor}")
    if broken or duplicates:
        return 1
    print("every ToC link resolves to an anchor GitHub actually emits")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
