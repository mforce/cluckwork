# Fix increment 2 — review findings on head `a1966740`

CodeRabbit raised four. **Three are real; one is stale and the driver is refuting it on the thread —
do not "fix" that one.**

## 1. `git checkout -- docs/images/` is too broad (Major — real)

`01-implementer-runbook.md:70` tells the implementer to restore the main checkout with
`git checkout -- docs/images/`. That discards **every** tracked modification in that directory, not only
what the capture regenerated. If someone had an unrelated in-progress image edit there, this destroys it
silently.

You already ran it, on a clean tree, so nothing was lost — but the instruction ships in a plan directory
that the repo keeps as a record and that the next person may copy.

Rewrite that step: assert `docs/images/` is clean **before** capturing (abort with a clear message if it
is not), and afterwards restore or delete only the specific generated paths by name. Keep it to the
runbook text; there is no script to change.

## 2. Markdown lint (real)

- `01-implementer-runbook.md:96` begins with `#664`, which markdownlint reads as a malformed ATX heading
  (MD018). Reword so the line does not start with `#`, keeping the reference readable.
- `01-implementer-runbook.md:190` opens a fenced block with no language. It contains a PR title, so tag
  it `text`.

## 3. Unchecked checkbox (real, cosmetic)

`02-fix-increment-1.md:10` lists a criterion as `- [ ]` while the text says **met**. Tick it.

## 4. NOT a defect — the alt text finding is stale. Do not change it.

CodeRabbit says `README.md:12`'s alt text names the trend and stock panels while the capture is 1280×800
and cuts them off. **That was true of the first commit and was fixed in `a1966740`**, the very head it
reviewed — it appears to have reasoned from the screenshots spec's stated viewport rather than from the
committed file.

Driver-verified just now:

```
$ sed -n '12p' README.md
![The dashboard: today's capture-status tiles by flock, a 14-day production trend,
  stock on hand by grade, and recent sales orders](docs/images/dashboard.png)
$ identify -format "%wx%h" docs/images/dashboard.png
1280x1180
```

All four named panels are in frame. **Leave `README.md` alone.** The driver replies on that thread with
this evidence; you do not need to.

## Gates

**G1**, **G2**, **G3** — unchanged from baseline. This slice still touches no application code; three of
the four changes are markdown and the fourth is runbook prose.

## Commit and push

```bash
git add docs/plans/
git commit -m "docs: narrow the image-restore step and fix markdown lint (#665 review)"
git push origin chore/screenshot-pipeline-and-conventions
```

Do NOT merge, do NOT mark ready or draft, do NOT reply on GitHub.

## Report back

Commit SHA, `git status --short`, G1/G2/G3, and confirmation you left `README.md` untouched.
