# 0007. Authenticate dashboard users with ASP.NET Identity JWTs and ingest clients with hashed API keys

- **Status:** Accepted
- **Date:** 2026-09-15
- **Deciders:** project owner

## Context and Problem Statement

Two very different callers use the API: humans in the dashboard and machines sending events. Each needs a tenant-scoped identity with minimal setup, and secrets must not be recoverable from the database.

## Decision Drivers

- FR-19 and FR-23: user login with roles; API keys shown once and revocable.
- NFR-07: hashed keys, 15-minute access tokens, per-key rate limits.
- NFR-10: no external identity provider container required.
- Portfolio value: demonstrate correct secret handling without over-building.

## Considered Options

1. ASP.NET Core Identity with JWT bearer (15 min access, 7 d refresh) for users; per-tenant API keys `crm_live_<32 base62>` stored as SHA-256 hash plus 8-character prefix for ingest.
2. Keycloak (OIDC) for users plus the same API key scheme.
3. No authentication; tenant selected by a header.

## Decision Outcome

Chosen option: "ASP.NET Identity JWT plus hashed API keys", because it ships with the framework, keeps the compose file small, and still demonstrates the important properties: short-lived tokens, refresh rotation, one-way key storage, constant-time comparison, and revocation. Keycloak is realistic for enterprises but adds a container and configuration that distract from slice 1. No auth fails NFR-07 and undermines the tenancy story.

### Positive consequences

- Zero extra infrastructure; login works on first `docker compose up` after seeding.
- Key resolution is cached in Redis by prefix (10 min) so the ingest hot path avoids SQL.
- Roles (`Admin`, `Viewer`) are JWT claims; authorization policies are declarative.

### Negative consequences

- No SSO or MFA; acceptable for a portfolio scope and easy to replace with OIDC later because the API only consumes bearer tokens.
- Users belong to exactly one tenant in slice 1.

## Consequences

Claims: `sub`, `tenant_id`, `role`. Rate limit: token bucket, 2,000 requests/min per API key, 429 on exceed. `LastUsedAt` on keys is updated at most once per minute. Tables are in the [identity data dictionary](../data-dictionary/identity.md).

The refresh token is carried in an httpOnly, Secure, `SameSite=Strict` cookie (`crm_refresh`, path `/v1/auth`) rather than in the response body or client-side storage, because a script can never read it: the access token is the only bearer credential exposed to JavaScript, which bounds the impact of an XSS finding to a 15-minute window.
