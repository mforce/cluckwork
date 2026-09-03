# public.AspNetUsers

## Columns

| Name | Type | Default | Nullable | Children | Parents | Comment |
| ---- | ---- | ------- | -------- | -------- | ------- | ------- |
| Id | uuid |  | false | [public.AspNetUserClaims](public.AspNetUserClaims.md) [public.AspNetUserLogins](public.AspNetUserLogins.md) [public.AspNetUserRoles](public.AspNetUserRoles.md) [public.AspNetUserTokens](public.AspNetUserTokens.md) |  |  |
| AccountId | uuid |  | false | [public.AspNetUserRoles](public.AspNetUserRoles.md) | [public.Accounts](public.Accounts.md) |  |
| DisplayName | text |  | true |  |  |  |
| Language | varchar(16) |  | true |  |  |  |
| MustChangePassword | boolean |  | false |  |  |  |
| UserName | varchar(256) |  | false |  |  |  |
| NormalizedUserName | varchar(256) |  | false |  |  |  |
| Email | varchar(256) |  | false |  |  |  |
| NormalizedEmail | varchar(256) |  | false |  |  |  |
| EmailConfirmed | boolean |  | false |  |  |  |
| PasswordHash | text |  | true |  |  |  |
| SecurityStamp | text |  | true |  |  |  |
| ConcurrencyStamp | text |  | true |  |  |  |
| PhoneNumber | text |  | true |  |  |  |
| PhoneNumberConfirmed | boolean |  | false |  |  |  |
| TwoFactorEnabled | boolean |  | false |  |  |  |
| LockoutEnd | timestamp with time zone |  | true |  |  |  |
| LockoutEnabled | boolean |  | false |  |  |  |
| AccessFailedCount | integer |  | false |  |  |  |
| CredentialEpoch | integer | 1 | false |  |  |  |
| DisabledAt | timestamp with time zone |  | true |  |  |  |
| DisabledBy | uuid |  | true |  |  |  |
| PreferredStepperUnit | varchar(16) |  | true |  |  |  |
| StepUpLogoutEpoch | integer | 0 | false |  |  |  |

## Viewpoints

| Name | Definition |
| ---- | ---------- |
| [Identity & access](viewpoint-3.md) | ASP.NET Identity, refresh tokens, per-flock role assignments. |

## Constraints

| Name | Type | Definition |
| ---- | ---- | ---------- |
| AspNetUsers_AccessFailedCount_not_null | n | NOT NULL "AccessFailedCount" |
| AspNetUsers_AccountId_not_null | n | NOT NULL "AccountId" |
| AspNetUsers_CredentialEpoch_not_null | n | NOT NULL "CredentialEpoch" |
| AspNetUsers_EmailConfirmed_not_null | n | NOT NULL "EmailConfirmed" |
| AspNetUsers_Email_not_null | n | NOT NULL "Email" |
| AspNetUsers_Id_not_null | n | NOT NULL "Id" |
| AspNetUsers_LockoutEnabled_not_null | n | NOT NULL "LockoutEnabled" |
| AspNetUsers_MustChangePassword_not_null | n | NOT NULL "MustChangePassword" |
| AspNetUsers_NormalizedEmail_not_null | n | NOT NULL "NormalizedEmail" |
| AspNetUsers_NormalizedUserName_not_null | n | NOT NULL "NormalizedUserName" |
| AspNetUsers_PhoneNumberConfirmed_not_null | n | NOT NULL "PhoneNumberConfirmed" |
| AspNetUsers_StepUpLogoutEpoch_not_null | n | NOT NULL "StepUpLogoutEpoch" |
| AspNetUsers_TwoFactorEnabled_not_null | n | NOT NULL "TwoFactorEnabled" |
| AspNetUsers_UserName_not_null | n | NOT NULL "UserName" |
| FK_AspNetUsers_Accounts_AccountId | FOREIGN KEY | FOREIGN KEY ("AccountId") REFERENCES "Accounts"("Id") ON DELETE RESTRICT |
| PK_AspNetUsers | PRIMARY KEY | PRIMARY KEY ("Id") |
| AK_AspNetUsers_Id_AccountId | UNIQUE | UNIQUE ("Id", "AccountId") |

## Indexes

| Name | Definition |
| ---- | ---------- |
| PK_AspNetUsers | CREATE UNIQUE INDEX "PK_AspNetUsers" ON public."AspNetUsers" USING btree ("Id") |
| EmailIndex | CREATE UNIQUE INDEX "EmailIndex" ON public."AspNetUsers" USING btree ("AccountId", "NormalizedEmail") |
| UserNameIndex | CREATE UNIQUE INDEX "UserNameIndex" ON public."AspNetUsers" USING btree ("AccountId", "NormalizedUserName") |
| AK_AspNetUsers_Id_AccountId | CREATE UNIQUE INDEX "AK_AspNetUsers_Id_AccountId" ON public."AspNetUsers" USING btree ("Id", "AccountId") |

## Relations

```mermaid
erDiagram

"public.AspNetUserClaims" }o--|| "public.AspNetUsers" : "FOREIGN KEY (#quot;UserId#quot;) REFERENCES #quot;AspNetUsers#quot;(#quot;Id#quot;) ON DELETE CASCADE"
"public.AspNetUserLogins" }o--|| "public.AspNetUsers" : "FOREIGN KEY (#quot;UserId#quot;) REFERENCES #quot;AspNetUsers#quot;(#quot;Id#quot;) ON DELETE CASCADE"
"public.AspNetUserRoles" }o--|| "public.AspNetUsers" : "FOREIGN KEY (#quot;UserId#quot;) REFERENCES #quot;AspNetUsers#quot;(#quot;Id#quot;) ON DELETE CASCADE"
"public.AspNetUserRoles" }o--|| "public.AspNetUsers" : "FOREIGN KEY (#quot;UserId#quot;, #quot;AccountId#quot;) REFERENCES #quot;AspNetUsers#quot;(#quot;Id#quot;, #quot;AccountId#quot;) ON DELETE CASCADE"
"public.AspNetUserTokens" }o--|| "public.AspNetUsers" : "FOREIGN KEY (#quot;UserId#quot;) REFERENCES #quot;AspNetUsers#quot;(#quot;Id#quot;) ON DELETE CASCADE"
"public.AspNetUsers" }o--|| "public.Accounts" : "FOREIGN KEY (#quot;AccountId#quot;) REFERENCES #quot;Accounts#quot;(#quot;Id#quot;) ON DELETE RESTRICT"

"public.AspNetUsers" {
  uuid Id
  uuid AccountId FK
  text DisplayName
  varchar_16_ Language
  boolean MustChangePassword
  varchar_256_ UserName
  varchar_256_ NormalizedUserName
  varchar_256_ Email
  varchar_256_ NormalizedEmail
  boolean EmailConfirmed
  text PasswordHash
  text SecurityStamp
  text ConcurrencyStamp
  text PhoneNumber
  boolean PhoneNumberConfirmed
  boolean TwoFactorEnabled
  timestamp_with_time_zone LockoutEnd
  boolean LockoutEnabled
  integer AccessFailedCount
  integer CredentialEpoch
  timestamp_with_time_zone DisabledAt
  uuid DisabledBy
  varchar_16_ PreferredStepperUnit
  integer StepUpLogoutEpoch
}
"public.AspNetUserClaims" {
  integer Id
  uuid UserId FK
  text ClaimType
  text ClaimValue
}
"public.AspNetUserLogins" {
  text LoginProvider
  text ProviderKey
  text ProviderDisplayName
  uuid UserId FK
}
"public.AspNetUserRoles" {
  uuid UserId FK
  uuid RoleId FK
  uuid AccountId FK
}
"public.AspNetUserTokens" {
  uuid UserId FK
  text LoginProvider
  text Name
  text Value
}
"public.Accounts" {
  uuid Id
  varchar_120_ Name
  varchar_64_ TimeZoneId
  varchar_32_ Locale
  varchar_3_ DefaultCurrencyCode
  varchar_8_ DefaultCurrencySymbol
  integer DefaultCurrencyMinorUnit
  varchar_16_ UnitSystem
  varchar_16_ FirstDayOfWeek
  varchar_32_ DateFormatOverride
  varchar_32_ TimeFormatOverride
  varchar_32_ Brand
  boolean IsActive
  integer Version
  uuid AccountId
  varchar_16_ DefaultStepperUnit
  varchar_32_ Slug
  varchar_24_ WorkerSaleAllocationPolicy
}
```

---

> Generated by [tbls](https://github.com/k1LoW/tbls)
