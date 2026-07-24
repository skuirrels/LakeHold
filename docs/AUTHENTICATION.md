# Authentication and authorisation

The plan for closing what was the largest gap in the product: the API had no authentication of any
kind, and tenant identity was the `{tenantSlug}` segment of a URL, so anyone who could reach the API
was every tenant.

**Every step of this plan has now landed.** What remains is one operator decision — setting
`Lakehold:Auth:RequireAuthentication` — plus the follow-ups listed under [Status](#status). See that
section for what each step delivered and what is still open.

This document is the specification and the running record of what has landed. It was written to be
worked one step at a time — each step independently shippable, independently testable, and leaving
the product working. Nothing here required the whole plan to be finished before any of it was useful.

The broader HTTP surface that sits on top of this — time travel, maintenance, and the rest of the
lakehouse — is specified in [`PUBLIC-API.md`](PUBLIC-API.md), which treats auth as its gate.

## The central change

Everything else is detail. Before this work:

```csharp
// Lakehold.Api/Endpoints/LakehouseEndpoints.cs
tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/query", ExecuteAsync);
```

The tenant was a **route parameter**, and `LakehouseService` believed it. Authentication means the
tenant comes from the **credential**, and the route segment is validated against it rather than
trusted. Until that inverts, every other control is decoration on an open door.

That inversion has landed: `LakeholdAuthorizationFilter` resolves the credential, validates the
route's tenant and catalog against it, and refuses a mismatch as a 404. The route segment is now
checked, not believed — for every credential that is presented. A request with **no** credential
still falls back to trusting the route until an operator sets `RequireAuthentication`.

Two invariants govern how far this goes:

- **Invariant 4 stays intact.** Isolation remains structural — a session can only reference the
  catalog attached to it. Authentication decides *which* catalog gets attached. It never becomes a
  filter over submitted SQL, because SQL parsing as a security boundary is a losing game.
- **Capability is expressed as attachment where it can be.** A credential that must not write should
  produce a session whose catalog is attached read-only (invariant 9), not a permission check that
  clever SQL might route around. This is the single most important design idea in this document.

## What we are defending against

Stated plainly, because a control that does not name its threat tends to be the wrong control:

| Threat | Today | After |
|---|---|---|
| Anyone with network reach reads every tenant's data | Trivial | Requires a credential |
| A credential for tenant A reads tenant B | Trivial over HTTP | Refused; the credential names the tenant |
| A credential shared for one catalog reads another in the same tenant | No tokens exist | Refused when the token is catalog-scoped |
| A read-only consumer writes to the lake | Nothing prevents it | Catalog attached read-only |
| A leaked credential cannot be withdrawn | No credentials exist | Revoked centrally, affects HTTP and the wire endpoint together |
| An audit trail says what ran but not who | `QueryRun` has no principal | Principal recorded per statement |

Explicitly **not** in scope: protecting a tenant from its own users (that is roles, phase 4), and
protecting rows within a catalog (row/column security, which stays on the far roadmap).

## Why tokens before SSO

The obvious instinct is OIDC first, because humans log in. It is the wrong order here.

Lakehold's pitch includes **air-gapped deployment**. An OIDC-first design makes an identity provider
a hard dependency of a product whose entire argument is that it does not need anyone else's
infrastructure. Tokens need nothing external, work identically on a laptop and in a sealed network,
and are what the machine consumers already need: CI, the CLI, the future `Lakehold.Client`, and the
PostgreSQL wire endpoint, which today carries its own separate credential scheme precisely because
none existed.

SSO follows in phase 3 for the workbench, where humans are, and air-gapped installs simply never
enable it.

## How the peers do it

Recorded because both informed the design, and because the differences are instructive.

**ClickHouse** — users and roles are SQL objects (`CREATE USER`, `GRANT`), with pluggable
authentication: password, LDAP with directory groups mapped to internal roles, Kerberos, SSL client
certificates matched on `CommonName`, SSH keys, or an external HTTP service. RBAC reaches
database/table/column, and **quotas bound query complexity, memory, and result size** as part of
access control rather than beside it.

Taken: never build an identity store for humans when a directory can be delegated to. Also that on
an analytics engine, "may query" and "may consume the machine" are the same question — worth
revisiting once tokens exist, since `MaxRowsPerResult` and the statement timeout are already
per-node rather than per-principal.

Not taken: grants as SQL objects. ClickHouse can do that because its catalog *is* the database.
Lakehold's control plane is the natural home, and access rules living inside DuckLake would have to
survive backup, restore, and eject — three operations that would then all need to reason about
security state.

**MotherDuck** — humans use SSO/OIDC on Business and above; machines use tokens. The instructive
part is that tokens carry **capability**: a default read/write token, or a **read-scaling** token
that cannot write and fans out across read replicas. Organisations group users for administration,
sharing, and billing.

Taken: capability belongs on the credential, and it is enforced by how the session is provisioned
rather than by a check on each statement. That maps directly onto read-only attachment.

---

## Phase 1 — API tokens

The foundation. Everything after this is additive.

### Entity

New control-plane entity, alongside `Tenant` and `LakeCatalog` in
`src/Lakehold.ControlPlane/Model/Entities.cs`:

```csharp
public enum TokenScope
{
    /// <summary>Acts as one tenant. The overwhelming majority of tokens.</summary>
    Tenant,

    /// <summary>Provisions tenants and catalogs. Cannot itself query data — see below.</summary>
    Instance,
}

public sealed class ApiToken
{
    public int Id { get; set; }

    public TokenScope Scope { get; set; }

    /// <summary>Null for an instance-scoped token, which belongs to no tenant.</summary>
    public int? TenantId { get; set; }

    /// <summary>
    ///     Optional least-privilege narrowing for a tenant-scoped token: null grants every catalog in
    ///     the tenant, a value restricts the token to that one catalog. This is *subject*, not
    ///     capability — orthogonal to <see cref="Scope"/>, and always null for an instance token.
    /// </summary>
    public string? CatalogName { get; set; }

    /// <summary>Human-facing label. Not a secret and not an identifier.</summary>
    public required string Name { get; set; }

    /// <summary>The token's public prefix, used to find the row before verifying the secret.</summary>
    public required string Prefix { get; set; }

    /// <summary>SHA-256 of the full token, hex-encoded. The token itself is never stored.</summary>
    public required string SecretHash { get; set; }

    public bool ReadOnly { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public DateTimeOffset? RevokedUtc { get; set; }
    public DateTimeOffset? LastUsedUtc { get; set; }

    public Tenant? Tenant { get; set; }
}
```

Adding an entity needs no migration: the control plane creates missing tables additively at start-up,
and `AdditiveSchemaTests` covers exactly this case. Add a test there for the new table rather than
assuming it.

### Token subject: tenant, optionally narrowed to a catalog

`Scope` is *capability* — provision (`Instance`) versus use (`Tenant`). *Subject* — which tenant, and
optionally which catalog — is a separate axis, and the two must not collapse into one enum. A third
`TokenScope.Catalog` would force the question "does a catalog token also imply a tenant?" and tangle
the two; a `CatalogName` on the row keeps them orthogonal.

The isolation boundary is the catalog, not the tenant: a `Duckling` attaches one catalog, and a tenant
is only the ownership grouping around its `Catalogs`. So the two subjects worth expressing are **the
whole tenant** — the workbench, internal apps, an admin doing tenant-wide work — and **exactly one
catalog, usually read-only** — a partner, a BI dashboard, anything given least privilege.
`CatalogName is null` is the first; a value is the second. Neither pure form suffices on its own:
tenant-only hands a multi-catalog tenant's every catalog to a consumer that needed one, and
catalog-only cannot express the admin and workbench paths that legitimately span the tenant.

Enforcement is by attachment, as everywhere else: a catalog-scoped token can only produce a session
that attaches its one catalog, so this composes with read-only (phase 2) rather than competing with
it. Two consequences fall out cleanly:

- **Cross-catalog shares still work.** A catalog-scoped token attaching `X` still reads whatever
  read-only shares `X` is configured to attach (invariant 9). The share is a property of the catalog,
  not a grant on the token, so there is nothing to widen here.
- **Arbitrary subsets are a later, additive change.** A single nullable `CatalogName` covers both real
  cases. "These three of ten catalogs" would be a `TokenCatalog` join table, added additively if and
  when someone asks — not before.

Phase 1 need not *issue* catalog-scoped tokens, but it bakes in the axis: the column exists and the
enforcement check runs from the start, a no-op while null. That makes least privilege the default the
day sharing is needed, with no token-model migration and no user retrained away from "a token is the
whole tenant".

### Token format

```
lkh_<tenant-slug>_<43 chars base64url>      // tenant-scoped; 32 random bytes
lkh_admin_<43 chars base64url>              // instance-scoped
```

- **The prefix is `lkh_<tenant>_`, or `lkh_admin_`,** and is stored in the clear. A tenant slug of
  `admin` must therefore be reserved, or a tenant could be created whose tokens are indistinguishable
  from instance-scoped ones at a glance. Reject it at tenant creation. It makes the row findable with an
  indexed lookup instead of hashing against every row, and it makes a leaked token identifiable in a
  log or a paste without being usable.
- **The secret is 32 bytes from `RandomNumberGenerator`.**
- **Stored as SHA-256, hex.** Deliberately *not* bcrypt/argon2: those exist to make brute force
  expensive against low-entropy human passwords. A 256-bit random secret has no brute force to
  defend against, and a slow KDF on every request would be a self-inflicted denial of service. This
  is the same reasoning behind GitHub's and Stripe's token formats.
- **Verified with `CryptographicOperations.FixedTimeEquals`** over the hashes.
- **Shown once, at creation.** The API returns the token exactly once and can never return it again,
  which is what makes "stored hashed" true rather than aspirational.

### Presentation

```
Authorization: Bearer lkh_demo_…
```

### Resolution and enforcement

One place, so there is one thing to get right:

1. Authentication middleware resolves `Bearer` → `ApiToken` → `Tenant`, rejecting revoked, expired,
   and unknown tokens identically (`401`, no detail about which).
2. The resolved principal is exposed as a scoped
   `ILakeholdPrincipal { TenantId, TenantSlug, CatalogName, IsReadOnly }`, where a null `CatalogName`
   means every catalog in the tenant.
3. `LakehouseService` takes the principal rather than a bare `tenantSlug`. A route slug that does not
   match the principal's tenant is a **404, not a 403** — a 403 confirms the tenant exists. A
   catalog-scoped principal whose `CatalogName` does not match the route's catalog is the same **404**,
   for the same reason.
4. `LastUsedUtc` is updated opportunistically, not on the request path. A write per request would put
   the control plane in front of every query for a field nobody reads in real time.

### Provisioning: the one thing that cannot be tenant-scoped

Creating a tenant is the operation with no tenant to be scoped to, and it is not hypothetical: with
demo seeding correctly disabled in production, a fresh deployment starts empty and there is currently
**no API for creating a tenant or a catalog at all**. Authentication and provisioning have to be
designed together, because the first token a deployment issues is the one that has nowhere to belong.

Hence `TokenScope.Instance`. The important decision is what that scope may *do*:

> **An instance-scoped token provisions. It does not query.**

It can create, list, and delete tenants and catalogs, and mint tenant-scoped tokens. It cannot
execute a statement, read a catalog, run maintenance, or eject. Those all require a tenant-scoped
token, so the property that makes the whole design work — *the credential names the tenant whose data
is reachable* — holds for every path that touches data.

The obvious objection is that an instance token can mint itself a tenant token and read anything.
True, and it is the right trade rather than a hole:

- **Escalation leaves a record.** Minting a token is an auditable, revocable event with a name and a
  timestamp. Silent data access through an admin credential is neither.
- **The blast radius of a leak differs.** A stolen instance token is a provisioning problem someone
  can see in the token list; a stolen all-powerful token is an undetectable data breach.
- **It keeps one rule instead of two.** Every data path checks tenant scope. There is no "unless the
  caller is an admin" branch in `LakehouseService`, which is exactly the kind of exception that is
  correct when written and wrong two refactors later.

The cost is one extra step for an operator doing something by hand, which is the correct place to
put the friction.

### Provisioning endpoints

These do not exist yet, and this spec is where they get their shape:

```
POST   /api/tenants                        → instance scope; creates a tenant
GET    /api/tenants                        → instance scope; tenant scope sees only its own
DELETE /api/tenants/{tenant}               → instance scope
POST   /api/tenants/{tenant}/catalogs      → instance scope
DELETE /api/tenants/{tenant}/catalogs/{c}  → instance scope
```

`GET /api/tenants` is the one that already exists and is currently unauthenticated — it lists every
tenant on the node. Under this model a tenant-scoped token sees exactly one entry and an
instance-scoped token sees all of them.

Deleting a tenant or catalog needs the same care as destructive maintenance (invariant 10): it must
not remove data as a side effect of removing a record. Deleting a catalog record should detach it and
leave the DuckLake metadata and Parquet in place, with removal of the data itself a separate,
explicit operation. A `DELETE` that silently destroys a lakehouse is not a control-plane operation.

### Bootstrap

The chicken-and-egg problem, and it must be answered before the first line of code: if the API needs
a token, where does the first one come from?

- On first start with **no tokens in the database**, the API mints an **instance-scoped** token,
  writes it to the log **once**, and never again. It has to be instance-scoped: on a production node
  there is no tenant for it to belong to, and minting the first tenant is the job it exists for.
- `Lakehold__BootstrapToken` in the environment overrides it, for deployments that provision
  credentials externally and cannot scrape a log.
- The bootstrap path runs only when the token table is empty, so it cannot be used to mint a second
  admin credential on a running deployment.

This is what closes the gap the production image exposed. A fresh deployment becomes:

```bash
docker compose -f compose.production.yaml up -d          # read the bootstrap token from the log
curl -X POST …/api/tenants        -H 'Authorization: Bearer lkh_admin_…'   -d '{"slug":"acme"}'
curl -X POST …/api/tenants/acme/catalogs -H 'Authorization: Bearer lkh_admin_…' -d '{"name":"analytics"}'
curl -X POST …/api/tenants/acme/tokens   -H 'Authorization: Bearer lkh_admin_…' -d '{"name":"bi"}'
```

### Endpoints

```
POST   /api/tenants/{tenant}/tokens        → creates; returns the token once
GET    /api/tenants/{tenant}/tokens        → lists metadata; never the secret
DELETE /api/tenants/{tenant}/tokens/{id}   → revokes
```

### Acceptance for phase 1

- A request with no token is refused; with a valid token succeeds.
- A token for tenant A cannot reach tenant B's catalog, by slug or by any other route.
- A token narrowed to catalog `X` cannot reach catalog `Y` in the same tenant, and the refusal is the
  same 404 as a cross-tenant one.
- A revoked or expired token is refused.
- The token never appears in a response body after creation, in a log, or in `QueryRun`.
- The workbench still works, which means the UI must send the token — see "Open questions".
- **An instance-scoped token cannot execute a query**, and the refusal comes from the same check
  every data path uses rather than from a special case.
- A tenant named `admin` cannot be created.

---

## Phase 2 — Read-only capability by attachment

Small, and the highest ratio of safety to effort in this document.

`ApiToken.ReadOnly` produces a `CatalogDescriptor` with `ReadOnly = true`, so the session's catalog is
attached read-only and DuckDB refuses writes. Not a policy check — the engine is simply not holding a
writable handle.

The one thing to get right: `DucklingPool` keys sessions **by catalog name**, so a read-only session
and a read-write session for the same catalog would collide and whichever started first would win.
The pool key has to include the attachment mode. Without that, this phase is worse than not doing it,
because a read-only token would silently get a writable session.

### Acceptance

- A read-only token executing `INSERT` is refused by the engine, not by a string check.
- A read-only and a read-write token against the same catalog get different sessions.
- The read-only session still serves reads normally, including through the wire endpoint.

---

## Phase 3 — OIDC for the workbench

Standard ASP.NET Core JWT bearer against whatever the operator runs — Keycloak, Entra, Authentik,
Auth0. Configuration is authority + audience; absent configuration, the whole path stays off and the
air-gapped story is unchanged.

Mapping an identity to tenants is the real work, not the JWT validation. Start with the simplest
thing that is honest: a claim naming the tenant, configurable in name, with a `TenantMember` table if
per-user membership is needed. Do not invent group syncing until someone asks.

Both schemes coexist: tokens for machines, OIDC for humans, one `ILakeholdPrincipal` behind both so
nothing downstream knows the difference.

---

## Phase 4 — Roles

`owner` / `editor` / `reader` per tenant, then per-catalog grants if they are asked for. Maintenance
and eject are owner operations; querying is a reader operation. `ReadOnly` from phase 2 becomes the
degenerate case of `reader`.

Row and column security stays where the roadmap has it — later, and probably as generated views
rather than an engine feature.

---

## Converging the wire endpoint

`Lakehold:PgWire:TenantPasswords` is configuration-based and was shipped as an explicit stopgap. It
has become the same token store, so that **revoking a credential closes the BI tool and the API
together** rather than one of them.

The mechanics needed care. PostgreSQL's MD5 exchange requires the server to know the plaintext, which
a hashed token store deliberately does not. The options were, in preference order:

1. **Accept the token as a cleartext password over TLS.** The client sends the token, the server
   hashes and compares. Requires `RequireTls`, and is how most token-bearing database endpoints work.
2. **SCRAM-SHA-256** with a stored verifier, which is the modern PostgreSQL mechanism and avoids
   plaintext entirely — more work, and worth it if the endpoint is to be exposed beyond a VPC.
3. Keep MD5 for configured per-tenant passwords as a legacy path, and tokens only over TLS.

**Option 1 shipped, with option 3's coexistence.** `Lakehold:PgWire:AllowTokenAuthentication` is off
by default; enabling it switches the exchange to cleartext, because a token cannot answer an MD5
challenge, and a presented value is tried against the token store first and the tenant's configured
password second. Because that puts a credential on the wire, the API **refuses to start** when it is
enabled without `RequireTls` (or an explicit `AllowCleartextPassword` for a trusted network) — a
misconfiguration fails loudly rather than leaking quietly. A token must name the connection's tenant
and honour any catalog narrowing, and a read-only token attaches the catalog read-only, so the wire
endpoint enforces exactly what the HTTP path does.

SCRAM-SHA-256 remains the better mechanism and stays a follow-up.

---

## Audit

`QueryRun` now records who ran a statement as well as what ran: `TokenId` is nullable — for the
pre-auth history that already existed — and carries no foreign key, so revoking or deleting a token
never takes its audit trail down with it. The history API returns the id and, best-effort, the
token's label; the workbench shows it beside each run. Half an audit trail is the kind of thing that
passes review until the day it matters.

---

## Open questions

To settle before or during the step they block, not before starting:

1. **How does the workbench hold a token?** *Partly resolved.* It holds one in `sessionStorage`,
   cleared when the tab closes, sent only to `/api`. That is deliberately not `localStorage`, and
   deliberately not the final answer: OIDC (phase 3) is, because it keeps a long-lived Lakehold
   credential out of the browser entirely. A short-lived cookie exchanged for a token remains the
   other option if operators want the workbench usable without an IdP.
2. **Do tokens scope to a catalog, or only to a tenant?** *Resolved: both, layered.* A token belongs
   to a tenant and may optionally be narrowed to one catalog via `ApiToken.CatalogName` (null = the
   whole tenant). Subject stays separate from `TokenScope`, which remains purely capability, and the
   enforcement check ships dormant in phase 1. See "Token subject: tenant, optionally narrowed to a
   catalog" under phase 1.
3. **Per-principal quotas?** ClickHouse treats them as access control. `MaxRowsPerResult` and the
   statement timeout are per-node today. Not phase 1, but the entity should not make it awkward.
4. **Rate limiting on authentication attempts.** The wire endpoint counts failures
   (`lakehold.pgwire.auth.failures`); the HTTP path will need the same, and probably a lockout.

## Order of work

Each step ships on its own and leaves the product working:

| Step | Deliverable | Gate | State |
|---|---|---|---|
| 1 | `ApiToken` entity, additive-schema test, token generation and hashing | Unit tests on format and verification | Done |
| 2 | Middleware, `ILakeholdPrincipal` (incl. catalog narrowing), `LakehouseService` takes the principal | Cross-tenant and cross-catalog refusal test | Done |
| 3 | Token management endpoints and bootstrap | Token shown once; revocation effective | Done |
| 3b | Provisioning endpoints for tenants and catalogs | A fresh deployment can be set up with only the bootstrap token | Done |
| 4 | Workbench sends credentials | Question 1 answered for machines; OIDC is the durable answer | Done |
| 5 | Read-only attachment, pool key includes mode | `INSERT` refused by the engine | Done |
| 6 | `QueryRun.TokenId` and history surfacing | Audit shows the principal | Done |
| 7 | Wire endpoint on the token store | Revocation closes both surfaces | Done |
| 8 | OIDC | JWT validated; tenant claim mapped to a principal | Done |
| 9 | Roles | Maintenance restricted to owners | Done |

Steps 1–3 are the ones that changed the product from open to closable. Step 3b is what makes a
production deployment usable at all — before it, a fresh node started empty with no supported way to
add anything. Everything after is depth.

## Status

**Every step, 1 through 9, has landed.**

- **Step 1** — the `ApiToken` entity (with the `CatalogName` narrowing) and its `ControlPlaneContext`
  mapping, additive-schema coverage that recreates the table on an existing database, and
  `ApiTokenFactory` for generation, SHA-256 hashing, and constant-time verification. Unit-tested on
  format and verification.
- **Step 2** — `ILakeholdPrincipal` / `LakeholdPrincipal`, `ApiTokenAuthenticator` (resolves a bearer
  token to a principal; malformed, unknown, revoked, and expired are refused identically),
  `TenantAccessPolicy` (route validated against the credential; mismatch is a 404), and a
  `LakeholdAuthorizationFilter` on the `/api/tenants` group. Enforcement is realised as a group filter
  rather than by threading the principal through every `LakehouseService` signature; the principal is
  stashed on the request for the phases that need it downstream (read-only attachment, audit).
- **Step 3** — token management (`POST`/`GET`/`DELETE /api/tenants/{tenant}/tokens`) in
  `AdminEndpoints`, and `TokenBootstrap`: on first start with an empty token table the API mints an
  instance-scoped token and logs it **once**, with `Lakehold__BootstrapToken` overriding it for
  deployments that inject credentials externally. The mint runs only when the table is empty, so it
  cannot add a second admin credential to a running node.
- **Step 3b** — provisioning (`POST`/`DELETE /api/tenants`, `POST`/`DELETE
  /api/tenants/{tenant}/catalogs`). The reserved `admin` slug is refused at creation as well as in the
  factory. Deleting a tenant or catalog removes control-plane records and evicts the session; the
  DuckLake metadata and Parquet are deliberately left in place (invariant 10's reasoning applied to
  provisioning). `GET /api/tenants` is now scoped: an instance token sees every tenant, a tenant token
  sees only its own.
- **Step 4** — the workbench holds a token in `sessionStorage` (`AuthService`) and an
  `authInterceptor` attaches it to `/api` calls only. A header control sets and clears it. With no
  token set the UI behaves exactly as before, which is what keeps development token-less.
- **Step 5** — a read-only principal produces a read-only attachment: `LakehouseService` narrows the
  descriptor and `DucklingPool` now keys sessions by catalog **and attachment mode**, so a read-only
  and a read-write session for one catalog cannot collide. `EvictAsync` drops both modes.
- **Step 6** — `QueryRun.TokenId` (nullable, no foreign key, so pre-auth history survives and a
  revoked token does not take its audit trail with it), threaded through the HTTP and wire paths and
  surfaced in the history API and panel. `AdditiveSchema.EnsureModelColumnsAsync` applies the column
  to existing databases.
- **Step 7** — `Lakehold:PgWire:AllowTokenAuthentication` accepts an API token as the password,
  verified against the same store, so **revoking a credential closes the BI tool and the API
  together**. It uses the cleartext exchange by necessity (a hashed store cannot answer MD5's
  challenge) and the API refuses to start with it enabled outside TLS. Configured `TenantPasswords`
  keep working alongside it.
- **Step 8** — `LakeholdOidcOptions` plus standard JWT bearer validation, wired only when an authority
  is configured, so an air-gapped install never acquires an identity-provider dependency.
  `OidcPrincipal` maps a tenant claim (and optional role claim) onto the same `ILakeholdPrincipal`
  tokens produce, so nothing downstream distinguishes a human from a machine.
- **Step 9** — `TokenRole` (`Owner`/`Editor`/`Reader`) on the token and the principal. Maintenance,
  restore, and eject are owner operations (`RouteCapability.TenantOwner`); token management requires
  owner too; querying is a reader's. `Reader` implies read-only at issuance, so phase 2's flag is the
  degenerate case rather than a second thing to remember. The enum's default is `Owner`, which is what
  every credential minted before roles existed effectively was — so an upgrade changes nothing.

Route intent is declared as `RouteCapability` metadata (`TenantData`, `TenantOwner`, `TenantAdmin`,
`Instance`, `Listing`) and enforced in one place. Subject is always checked before capability, so an
unreachable tenant is a 404 and never a 403 that would confirm it exists.

**The remaining decision is the operator's.** `LakeholdAuthOptions.RequireAuthentication` still
defaults to **false**, so a request with no token falls back to trusting the route. Everything needed
to turn it on now exists — tokens can be issued, the workbench can present one, and the wire endpoint
shares the store — so a deployment closes the door by setting:

```json
{ "Lakehold": { "Auth": { "RequireAuthentication": true } } }
```

The default is left open deliberately: flipping it in a release would break every existing deployment
on upgrade, which is a decision to make per deployment rather than one to inherit.

### Still open

- **Question 1 remains partly open.** The workbench holds a bearer token in `sessionStorage`, which is
  better than `localStorage` but is still the interim answer. The durable one is OIDC (step 8), where
  the browser never holds a long-lived Lakehold credential, or a short-lived cookie exchanged for a
  token.
- **Question 3 (per-principal quotas)** is untouched. `MaxRowsPerResult` and the statement timeout
  remain per-node. The entity does not make it awkward to add.
- **Question 4 (rate limiting on authentication attempts)** is untouched on the HTTP path; the wire
  endpoint still counts failures. A lockout is the obvious next control.
- **SCRAM-SHA-256** for the wire endpoint, rather than cleartext-over-TLS, remains the better
  long-term mechanism.
- **Per-user membership** (`TenantMember`) is deliberately not built: the OIDC mapping is a single
  claim until someone asks for more.
