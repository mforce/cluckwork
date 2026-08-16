<!--
The PR TITLE is the release note. Squash-merge takes the commit subject from it,
and release-please parses that for the changelog and the bump — so a
non-conventional prefix silently costs the bump, with a green run. No local hook
sees the PR title.

    feat(scope): …   fix(scope): …   docs|chore|test|ci|build|style(scope): …
    feat!: …  or a BREAKING CHANGE: footer
-->

## What and why

<!-- What changed and the problem it solves, in a few lines. Link the issue.
     This body becomes the squashed commit in main — keep it short, and mind the
     `word((` parser trap: see CONTRIBUTING.md#commit-messages. -->

## How it was verified

<!--
Commands run and what they printed — not "tests pass". For a guard, name the
mutation you ran and which test went red. → docs/decisions/407-writing-a-guard.md
-->

## Checklist

Delete any line that does not apply — an inapplicable ticked box is noise.

- [ ] The **PR title** is a conventional commit, and is the release note I want.
- [ ] Tests cover the change, and I watched the new ones fail first.
- [ ] Any aggregate mutation bumps `Version` and has a parallel-race test.
- [ ] Any new guard was **mutation-checked**: green baseline, mutant red on that
      guard's own named assertion, baseline green after restoring.
- [ ] A new migration ships with regenerated `docs/schema/` in this PR (#417).
- [ ] A new Production boot guard or config key also updates the sim harness (#370).
- [ ] Docs updated in this PR — `specs/product/GLOSSARY.md` and the SPA Help page
      for anything user-visible.
- [ ] The slice is on its phase epic's checklist (#14 Phase 1.1, #15 Phase 1.5).
- [ ] A non-obvious decision is recorded in `docs/decisions/`, or there is none.
- [ ] No hardcoded credential, in application **or** test code.
- [ ] No hardcoded hosting-provider name in code, config, or a committed doc.
- [ ] Any new third-party Action is pinned to a **full commit SHA** with a
      trailing `# vX.Y.Z` comment.
- [ ] A package add or bump commits the regenerated lock file in the same commit.
