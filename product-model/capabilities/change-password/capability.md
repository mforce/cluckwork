---
availability:
- place: farm-web::farm
domain: account
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Access/Users/ChangeOwnPassword/ChangeOwnPasswordHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Auth/AuthEndpoints.Password.cs
- kind: code
  role: implementation
  target: web/src/routes/SetPasswordPage.tsx
---

# Change password

Change one's own password by proving the current one. It also replaces a one-time password the Product requires at sign-in. Every other device is signed out; this one stays signed in.
