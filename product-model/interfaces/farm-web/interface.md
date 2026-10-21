---
type: web
actors:
- visitor
- owner
- manager
- worker
- sales
- read-only
entryPoints:
- web: /
languages:
- en
- es
- tl
references:
- kind: code
  role: implementation
  target: web/src/App.tsx
- kind: code
  role: implementation
  target: web/src/routes/nav.tsx#navGroups
---

# Farm web app

The farm's web app, served from the same address as its API and installable to a
phone's home screen. People sign in to one farm and see the screens their role
can reach.

## Intent

One app for the barn, the office and the phone: capture on a phone, review and
administration on a wider screen, with the same role gates on both.
