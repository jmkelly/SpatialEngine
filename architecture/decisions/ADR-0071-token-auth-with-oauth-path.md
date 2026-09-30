---
status: proposed
date: 2026-09-16
deciders: maintainer + agent
summary: Token auth from username/password (opaque bearers, config users, SDK/CLI/workbench) with an OAuth2/OIDC issuer path reserved. (proposed)
---

# ADR-0071: Token auth from username/password, with an OAuth2/OIDC path

## Context

The host has one secret today: `Spatial:Admin:Token`
(`SPATIAL_ADMIN_TOKEN`). When set, mutation routes
(`PUT`/`DELETE /api/maps`, `POST /api/ingest`, the Esri admin
projection) require it as `Authorization: Bearer …` or `?token=`; when
unset, mutation routes are not mounted at all (ADR-0041 §6, ADR-0053).
Reads are anonymous.

That was the right bootstrap, but it does not survive first contact with
a real deployment of the current wedge (PostGIS as Esri REST in one
container):

- One shared static token cannot distinguish humans, scripts and the
  CLI; it cannot be rotated per user, expired, or revoked without
  restarting every client.
- There are no identities, so no per-user audit trail and no read gating
  when a dataset must not be public.
- Enterprise GIS buyers expect SSO next (Entra ID / Keycloak / ArcGIS
  Online OAuth2). Whatever we build now must not block that.

## Decision

**Phase 1 (this scope): username/password login issuing opaque bearer
tokens. Phase 2 (reserved): OAuth2/OIDC login alongside it via a second
issuer behind the same auth abstraction. No session cookies, no custom
password crypto, no auth state in the spatial stores.**

### Phase 1 — local users + opaque tokens

1. **User source (config-first, no new store).**
   `Spatial:Auth:Users[]` in host configuration: `{username,
   passwordHash, roles[]}`. Passwords are stored only as hashes —
   ASP.NET Core `PasswordHasher<T>` (PBKDF2) over the existing
   framework shared dependency, no new package. Env-var override for a
   single bootstrap admin (`SPATIAL_ADMIN_USERNAME` /
   `SPATIAL_ADMIN_PASSWORD_HASH`, or the legacy `SPATIAL_ADMIN_TOKEN`
   honoured as a still-valid bearer during migration). Cleartext
   passwords never appear in logs, errors or API responses.
2. **Token model (opaque, revocable, expiring).**
   `POST /api/auth/login {username, password} → {token, expiresAt}`.
   Tokens are 256-bit CSPRNG values, SHA-256-hashed at rest, with
   `expiresAt` (default 12 h, configurable `Spatial:Auth:TokenLifetime`)
   and `POST /api/auth/logout` + `POST /api/auth/refresh` (rotation:
   refresh mints a new token, revokes the old). `GET /api/auth/me`
   returns the caller's identity/roles for CLI and workbench use.
   Transport is `Authorization: Bearer …` (query `?token=` stays for
   backwards compat on existing admin routes only, never for login).
3. **Contracts (core-typed, package-free).**
   New faces in `Spatial.Contracts` (which takes no packages):
   `IAuthService` (`ValidateTokenAsync`, `LoginAsync`, `LogoutAsync`,
   `RefreshAsync`), `AuthIdentity`/`AuthToken` DTOs, `auth.failed` /
   `auth.unauthorized` / `auth.forbidden` `SpatialException` codes
   mapping to 401/403. HTTP shapes in `Spatial.Contracts.Http`. The
   implementation (`Spatial.Host` composition or a small
   `Spatial.Auth.Local` project) depends on contracts, never the reverse
   (principles 6–8).
4. **Enforcement.**
   Reads stay anonymous by default; every mutation route requires a
   valid bearer with the `admin` (or `writer`) role. Role checks are
   middleware/filter-level so neutral, Esri-admin and OGC-write paths
   share them. Failures are the structured codes above; the Esri
   envelope keeps its typed mapping (no `f=html` login pages).
5. **Clients.**
   TS + .NET SDKs gain `login/logout/refresh/me` helpers with
   drift-checked wire types; the CLI gains `auth login/logout` (token
   cached in `~/.spatial/token` with `0600`) and sends the bearer
   automatically; the workbench gains a login screen + token storage
   (memory-first, opt-in localStorage) and surfaces 401 by redirecting
   to login.

### Phase 2 — OAuth2/OIDC (reserved, not built here)

1. An `IAuthIssuer` abstraction with two implementations: `LocalIssuer`
   (phase 1) and `OidcIssuer` (authorization-code + PKCE for the
   workbench/CLI, client-credentials for service clients).
2. Config `Spatial:Auth:Issuers[]`: `{id, kind: local|oidc, authority,
   clientId, audience, …}` — Entra ID / Keycloak / Auth0 are just
   issuer rows; JWT validation uses the framework's
   `Microsoft.AspNetCore.Authentication.JwtBearer` (no custom JWT code).
3. Identity model already multi-issuer: `AuthIdentity {issuer, subject,
   username, roles[]}` so Esri `token` query-param interop maps to the
   same identity. Role mapping from OIDC claims (`roles`/`groups`) is
   config-declared.

### Non-goals (both phases)

API keys per dataset, row-level ACLs, LDAP bind, SAML, self-registration,
or storing auth state in PostGIS/demo stores — all future ADRs on
measured demand.

## Consequences

- First real multi-user story: per-user tokens with expiry/revocation,
  shared enforcement across neutral/Esri/OGC surfaces, SDK + CLI +
  workbench support — test-first with success, failure (bad password,
  expired, revoked) and cancellation coverage per repo policy.
- Migration is additive: existing `SPATIAL_ADMIN_TOKEN` deployments keep
  working until the operator defines users, then the static token is
  removed and login is the only path.
- OAuth2 later is a new issuer + config + tests, not a rewrite — but it
  is explicitly *not* promised in phase 1.

## Alternatives

- **JWTs minted locally in phase 1:** rejected — self-validating tokens
  complicate revocation before we have any revocation story; opaque
  tokens with a hashed server record are simpler and sufficient at this
  scale.
- **API keys only (no login):** rejected — solves scripts but not humans
  in browsers; login + bearer covers both.
- **Cookies/sessions:** rejected — the engine is headless with
  non-browser clients (CLI, SDKs, Esri clients); bearer tokens work
  everywhere cookies do not.
- **Full OIDC now:** rejected — doubles the scope (provider testing,
  PKCE flows, claim mapping) before the local identity model exists to
  hang it on.
