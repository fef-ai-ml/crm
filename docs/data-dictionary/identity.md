# Schema `identity`

Owner: Tenancy module. Auth rationale: [ADR 0007](../adr/0007-auth-model.md). ASP.NET Core Identity default tables (`AspNetUsers`, `AspNetRoles`, `AspNetUserRoles`, `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserTokens`, `AspNetRoleClaims`) live in this schema unchanged except for the additions below.

## Tenants

The one table without a `TenantId` column; its `Id` is the tenant id everywhere else.

| Column | Type | Null | Description |
|---|---|---|---|
| Id | uniqueidentifier | no | Primary key |
| Name | nvarchar(128) | no | Display name |
| Slug | varchar(64) | no | URL-safe unique identifier, `[a-z0-9-]{3,64}` |
| Status | varchar(16) | no | `Active`, `Suspended` |
| CreatedAt | datetime2(3) | no | |

Indexes: `PK_Tenants (Id)`; `UX_Tenants_Slug (Slug)` unique.

## ApiKeys

| Column | Type | Null | Description |
|---|---|---|---|
| Id | uniqueidentifier | no | Primary key |
| TenantId | uniqueidentifier | no | Kernel `Entity`; tenant-filtered |
| Name | nvarchar(128) | no | Label chosen by the admin |
| Prefix | char(8) | no | First 8 characters after `crm_live_`; shown in the UI and used as cache key |
| KeyHash | binary(32) | no | SHA-256 of the full key; the plaintext is never stored |
| CreatedByUserId | nvarchar(450) | no | FK to AspNetUsers(Id) |
| RevokedAt | datetime2(3) | yes | Non-null means the key is rejected |
| LastUsedAt | datetime2(3) | yes | Updated at most once per minute |
| CreatedAt | datetime2(3) | no | Kernel `Entity` |
| UpdatedAt | datetime2(3) | no | Kernel `Entity` |

Indexes: `PK_ApiKeys (Id)`; `UX_ApiKeys_Prefix (Prefix)` unique; `IX_ApiKeys_TenantId (TenantId)`.

Key format: `crm_live_` + 32 base62 characters. Verification: look up by prefix, compare SHA-256 in constant time, reject if `RevokedAt` is set.

## Users

`AspNetUsers` extended with:

| Column | Type | Null | Description |
|---|---|---|---|
| TenantId | uniqueidentifier | no | FK to Tenants(Id); a user belongs to exactly one tenant |
| DisplayName | nvarchar(128) | yes | |

Roles: `Admin` (full access), `Viewer` (read-only on profiles and segments). Indexes: `IX_AspNetUsers_TenantId (TenantId)`; `EmailIndex (NormalizedEmail)` unique, filtered on `[NormalizedEmail] IS NOT NULL` (migration `EnforceUniqueEmail`). Emails are unique across all tenants, not just within one: login (`AuthService.LoginAsync`) looks a user up by normalized email alone, before a tenant is known.

## RefreshTokens

| Column | Type | Null | Description |
|---|---|---|---|
| Id | uniqueidentifier | no | Primary key |
| UserId | nvarchar(450) | no | FK to AspNetUsers(Id) |
| TokenHash | binary(32) | no | SHA-256 of the opaque refresh token |
| ExpiresAt | datetime2(3) | no | Issued + 7 d |
| RevokedAt | datetime2(3) | yes | Set on rotation or logout |
| ReplacedByTokenId | uniqueidentifier | yes | Rotation chain |
| TenantId | uniqueidentifier | no | Kernel `Entity`; tenant-filtered |
| CreatedAt | datetime2(3) | no | Kernel `Entity` |
| UpdatedAt | datetime2(3) | no | Kernel `Entity` |

Indexes: `PK_RefreshTokens (Id)`; `UX_RefreshTokens_TokenHash (TokenHash)` unique; `IX_RefreshTokens_UserId (UserId)`.

Roles are seeded with fixed ids so they are stable across environments: `Admin` = `0192f0a0-0000-7000-8000-000000000001`, `Viewer` = `0192f0a0-0000-7000-8000-000000000002` (`TenancyDbContext.AdminRoleId` / `ViewerRoleId`).

Migration history for this schema lives in its own table, `identity.__EFMigrationsHistory_identity`, separate from other modules' history tables.
