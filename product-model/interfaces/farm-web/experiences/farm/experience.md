---
actors:
- owner
- manager
- worker
- sales
- read-only
access: authenticated
entryPoints:
- farm-web: /
references:
- kind: code
  role: implementation
  target: web/src/routes/AppLayout.tsx
- kind: code
  role: implementation
  target: web/src/routes/ProtectedRoute.tsx
---

# Farm work

Signed-in people of every role record the day, sell, review stock, history and reports, and manage their own account, each seeing what their role allows.
