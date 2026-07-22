# Authentication and authorisation

The plan for closing the largest gap in the product: the API has no authentication of any kind.
Tenant identity is the `{tenantSlug}` segment of a URL, so anyone who can reach the API is every
tenant.

This document is the specification and the running record of what has landed. It is written to be
worked one step at a time — each step is independently shippable, independently testable, and leaves
the product working. Nothing here requires the whole plan to be finished before any of it is useful.

## The central change

Everything else is detail. Today:

```csharp
// Lakehold.Api/Endpoints/LakehouseEndpoints.cs
tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/query", ExecuteAsync);
```

The tenant is a **route parameter**, and `LakehouseService` believes it. Authentication means the
tenant comes from the **credential**, and the route segment is validated against it rather than
trusted. Until that inverts, every other control is decoration on an open door.

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
public sealed class ApiToken
{
    public int Id { get; set; }
    public int TenantId { get; set; }

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

    public Tenant Tenant { get; set; } = null!;
}
```

Adding an entity needs no migration: the control plane creates missing tables additively at start-up,
and `AdditiveSchemaTests` covers exactly this case. Add a test there for the new table rather than
assuming it.

### Token format

```
lkh_<tenant-slug>_<43 chars base64url>      // 32 random bytes
```

- **The prefix is `lkh_<tenant>_`** and is stored in the clear. It makes the row findable with an
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
2. The resolved principal is exposed as a scoped `ILakeholdPrincipal { TenantId, TenantSlug, ReadOnly }`.
3. `LakehouseService` takes the principal rather than a bare `tenantSlug`. A route slug that does not
   match the principal's tenant is a **404, not a 403** — a 403 confirms the tenant exists.
4. `LastUsedUtc` is updated opportunistically, not on the request path. A write per request would put
   the control plane in front of every query for a field nobody reads in real time.

### Bootstrap

The chicken-and-egg problem, and it must be answered before the first line of code: if the API needs
a token, where does the first one come from?

- On first start with **no tokens in the database**, the API mints one for the demo tenant, writes it
  to the log **once**, and never again. Same shape as the demo seeding that already exists.
- `Lakehold__BootstrapToken` in the environment overrides it, for deployments that provision
  credentials externally and cannot scrape a log.

### Endpoints

```
POST   /api/tenants/{tenant}/tokens        → creates; returns the token once
GET    /api/tenants/{tenant}/tokens        → lists metadata; never the secret
DELETE /api/tenants/{tenant}/tokens/{id}   → revokes
```

### Acceptance for phase 1

- A request with no token is refused; with a valid token succeeds.
- A token for tenant A cannot reach tenant B's catalog, by slug or by any other route.
- A revoked or expired token is refused.
- The token never appears in a response body after creation, in a log, or in `QueryRun`.
- The workbench still works, which means the UI must send the token — see "Open questions".

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
should become the same token store, so that **revoking a credential closes the BI tool and the API
together** rather than one of them.

The mechanics need care. PostgreSQL's MD5 exchange requires the server to know the plaintext, which a
hashed token store deliberately does not. Options, in preference order:

1. **Accept the token as a cleartext password over TLS.** The client sends the token, the server
   hashes and compares. Requires `RequireTls`, which is now supported, and is how most token-bearing
   database endpoints work.
2. **SCRAM-SHA-256** with a stored verifier, which is the modern PostgreSQL mechanism and avoids
   plaintext entirely — more work, and worth it if the endpoint is to be exposed beyond a VPC.
3. Keep MD5 for configured per-tenant passwords as a legacy path, and tokens only over TLS.

This is a decision to make deliberately rather than by default, because it is the one place where the
token design and the protocol disagree.

---

## Audit

`QueryRun` records what ran, not who ran it. Add `TokenId` (nullable, for the pre-auth history that
already exists) and surface the principal in the history API and the workbench. Half an audit trail
is the kind of thing that passes review until the day it matters.

---

## Open questions

To settle before or during the step they block, not before starting:

1. **How does the workbench hold a token?** A single-page app storing a long-lived bearer token in
   `localStorage` is the standard bad answer. Options: a short-lived session cookie issued by the API
   in exchange for a token, or defer entirely and require OIDC for the UI while tokens serve machines.
   Blocks phase 1's acceptance criterion about the UI still working.
2. **Do tokens scope to a catalog, or only to a tenant?** Tenant-only is simpler and matches the
   isolation boundary. Catalog scoping is a real ask for sharing, and is easier to add before anyone
   depends on the shape.
3. **Per-principal quotas?** ClickHouse treats them as access control. `MaxRowsPerResult` and the
   statement timeout are per-node today. Not phase 1, but the entity should not make it awkward.
4. **Rate limiting on authentication attempts.** The wire endpoint counts failures
   (`lakehold.pgwire.auth.failures`); the HTTP path will need the same, and probably a lockout.

## Order of work

Each step ships on its own and leaves the product working:

| Step | Deliverable | Gate |
|---|---|---|
| 1 | `ApiToken` entity, additive-schema test, token generation and hashing | Unit tests on format and verification |
| 2 | Middleware, `ILakeholdPrincipal`, `LakehouseService` takes the principal | Cross-tenant refusal test |
| 3 | Token management endpoints and bootstrap | Token shown once; revocation effective |
| 4 | Workbench sends credentials | Question 1 answered |
| 5 | Read-only attachment, pool key includes mode | `INSERT` refused by the engine |
| 6 | `QueryRun.TokenId` and history surfacing | Audit shows the principal |
| 7 | Wire endpoint on the token store | Revocation closes both surfaces |
| 8 | OIDC | Workbench login against a real IdP |
| 9 | Roles | Maintenance restricted to owners |

Steps 1–3 are the ones that change the product from open to closed. Everything after is depth.
