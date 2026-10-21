---
availability:
- place: farm-web::public
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Auth/AuthEndpoints.Login.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Auth/LoginRequestValidator.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Infrastructure/Modules/Access/Identity/AccountLockout.cs
- kind: code
  role: implementation
  target: web/src/routes/Login.tsx
---

# Sign in

Sign in to one farm with its farm code, an email and a password. The farm code comes first because one email address can belong to people on several farms.
