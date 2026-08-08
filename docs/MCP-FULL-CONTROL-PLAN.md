# Controlling LakeHold from an agent, as a person

The implementation record for closing the distance between the original `src/Lakehold.Api/Mcp/`
surface and the goal:
**an operator drives the whole product from Codex or Claude Code, authenticated as a particular
user rather than as a shared machine token.**

Read [`MCP.md`](MCP.md) first — it is the specification and running record for the server itself, and
nothing here contradicts it. This document preserves the original gap analysis, records what landed,
and names what remains deliberately deferred. Like `MCP.md` and `AUTHENTICATION.md`, each phase was
independently shippable and left the product working.

Written 8 August 2026 against `feature/managed-connectors-styling`. The findings below were read out
of the source, not out of `MCP.md`; where the two disagree, that is noted and the source wins.

## Implementation status

Implemented on 8 August 2026:

- Phase 1: mandatory API audience, request-specific MCP resource audience, both RFC 9728 metadata
  locations, and advertised scopes.
- Phase 2: nullable member attribution, mutually exclusive actor kind, transport origin, PostgreSQL
  migration, history API, Workbench history, and end-to-end MCP token/member tests.
- Phase 3.1: optional pre-registered public MCP client and scopes, a bundled Keycloak development
  registration, and provider setup guidance. Phase 3.2 remains a post-adoption decision as planned.
- Phase 4 items 1 and 2: physical-layer/history inspection and saved-query tools. Snapshot-bound
  `plan_maintenance` / `apply_maintenance` also landed behind the persisted **Allow operator
  commands** tier.

Still deliberately blocked or undecided by this plan: the import content contract; CDC secret-reference
contract; long-running backup/restore Tasks reconciliation; and Phase 3.2's LakeHold authorization
server. Eject, credential minting, and instance provisioning remain intentionally withheld.

## What already works, so it is not re-litigated below

`McpAuthenticationFilter` already accepts two credentials, and the second one *is* the
acting-as-a-person path:

- an `lkh_`-prefixed API token resolved by `ApiTokenAuthenticator`
  ([`McpAuthenticationFilter.cs:53`](../src/Lakehold.Api/Mcp/McpAuthenticationFilter.cs)), and
- anything else, resolved from `HttpContext.User` through `MemberDirectory.ResolveAsync`
  ([`McpAuthenticationFilter.cs:62`](../src/Lakehold.Api/Mcp/McpAuthenticationFilter.cs)), which
  yields a principal carrying that person's `TenantMember` role.

`BrowserAuthentication` registers a `lakehold.jwt` scheme and a policy selector that forwards any
non-`lkh_` bearer to it, so a JWT minted by the configured issuer authenticates at `/mcp` today. Both
paths land in the same `CapabilityPolicy`, so a person gets exactly the capability their membership
row grants (invariant 21). The identity model is right. What is missing is a way for a client to
*obtain* the token, a reason to *trust* it, a record of *who used* it, and enough tools to be worth
the trip.

For data, `execute` plus a read-write credential is already complete control — arbitrary DDL and DML.
The coverage gap in phase 4 is the control plane, not the lakehouse.

---

## Phase 1 — make a user token safe to accept

Small, self-contained, and a prerequisite for every later phase. Nothing here needs an MCP client to
test.

### 1.1 Audience validation was off by default, and that was load-bearing

Before this plan was implemented, `ConfigureAudience` set `ValidateAudience = false` whenever
`Lakehold:Oidc:Audience` was empty
([`BrowserAuthentication.cs:107`](../src/Lakehold.Api/Auth/BrowserAuthentication.cs)).
At the time of the gap analysis, `IDENTITY-PROVIDER-SETUP.md` recorded that an empty audience accepted
every token that issuer minted. That was a documented sharp edge when the only consumers were the
workbench and the REST API.
It is a different thing now that `/mcp` accepts JWTs: a token minted for an unrelated application in
the same realm is accepted as an agent credential for the lakehouse.

The MCP authorization specification's central requirement of a resource server is that it reject a
token not issued *for it*. `McpResourceMetadata.Describe` already advertises a `resource` value
([`McpResourceMetadata.cs:118`](../src/Lakehold.Api/Mcp/McpResourceMetadata.cs)) that nothing
validates, which is the confused-deputy shape in full.

**Do:** make `Audience` required whenever `Lakehold:Oidc:Enabled` is true — fail startup with a
message naming the key, the same way the PgWire options guard already refuses an unsafe combination
in `Program.cs`. This follows the precedent set when `Lakehold:Auth:RequireAuthentication` was
removed: a protective control that defaults to off is a control that is not built.

**Also do:** honour RFC 8707 resource indicators — send `resource` on the authorization and token
requests in phase 3, and validate the audience against the MCP endpoint's own resource identifier
rather than a single shared audience string, so a workbench token and an MCP token are not
interchangeable.

**Test:** a token whose `aud` names another client is refused at `/mcp` with the same opaque
`WWW-Authenticate: Bearer` challenge as every other refusal — `McpAuthenticationFilterTests` already
owns that discipline and this is one more row in it.

### 1.2 The RFC 9728 path-suffixed metadata URL is not served

`McpResourceMetadata.Path` maps only `/.well-known/oauth-protected-resource`
([`McpResourceMetadata.cs:33`](../src/Lakehold.Api/Mcp/McpResourceMetadata.cs)). RFC 9728 locates the
document for a resource with a path component at the path-inserted form —
`/.well-known/oauth-protected-resource/mcp` for a resource at `/mcp`.

A client that follows the `resource_metadata` parameter on the 401 challenge is unaffected, because
the challenge cites the absolute URL. A client that probes the well-known location *before* making an
authenticated call — several do — gets a 404 and falls back to guessing.

**Do:** map both, deriving the suffixed path from `McpOptions.Route` so a non-default route stays
correct. Serve identical bytes from each.

**Test:** extend `McpResourceMetadataTests` — both URLs serve the same document, and the suffixed one
tracks a changed `Route`.

### 1.3 Advertise `scopes_supported`

PRM currently names a resource, the authorization servers, the bearer methods, and a display name.
Without `scopes_supported` a client requests the issuer's default scopes and may receive a token that
does not carry the claims `MemberDirectory` reads.

**Do:** populate it from `LakeholdOidcOptions.Scopes`.

---

## Phase 2 — an actor on the audit record

Independent of everything else, and worth doing on its own merits: **signing in as a particular
person currently makes attribution worse than using a machine token.**

`QueryRun` carries exactly one identity column, `TokenId`
([`Entities.cs:1487`](../src/Lakehold.ControlPlane/Model/Entities.cs)), and `MemberDirectory` returns
`TokenId: null` ([`MemberDirectory.cs:102`](../src/Lakehold.ControlPlane/Security/MemberDirectory.cs)).
Every statement an OIDC-authenticated person runs — through MCP *and* through the workbench today —
is recorded against nobody.

### `MCP.md` says this is blocked. It is not, any more.

`MCP.md`'s phase 5 records the MCP-origin marker as blocked because `AdditiveSchema` creates only
missing *tables* and the control plane has no path for a column added to an existing entity. That was
true when it was written and is stale now: `src/Lakehold.ControlPlane/Data/Migrations/` holds nine
generated EF migrations plus a model snapshot, and `ControlPlaneDatabase.MigrateAsync` calls
`Database.MigrateAsync()` under a PostgreSQL advisory lock. Adding a column is an ordinary migration.

**Do:**

- Add `MemberId` (nullable, no foreign key, for the same reason `TokenId` has none — a removed member
  must not take their audit trail down with them), `ActorKind`, and `Origin` to `QueryRun`, in one
  migration.
- Thread them through `QueryExecutionCoordinator` and `LakehouseService.ExecuteAsync` alongside the
  existing `TokenId`, and populate `Origin` at each transport: workbench, REST, PgWire, MCP.
- Surface the actor in the history endpoint and the workbench history panel.

This closes two items at once — the user attribution above, and the MCP-origin marker `MCP.md` lists
as outstanding. Update that phase-5 entry when it lands rather than leaving the blocked note in place.

**Test:** a query run through MCP under a member principal records that member and an MCP origin;
one run under an API token records the token id and its own origin; neither records both.

---

## Phase 3 — the OAuth flow that makes "as me" reachable

This is the phase the user-facing goal actually turns on.

Codex and Claude Code obtain a user token by driving OAuth 2.1 authorization-code with PKCE against
whichever authorization server the protected-resource metadata names. LakeHold names the IdP issuer
directly (`AuthorizationServers = [oidc.Authority]`) and is not itself an authorization server, so
the client must register with the IdP. That works only where **dynamic client registration**
(RFC 7591) is open, and Keycloak, Entra, and Okta all ship it closed. Today, therefore, the only way a
person's token reaches `/mcp` is pasting a JWT by hand, and it expires on the issuer's schedule.

Two ways forward, and they are not exclusive — the first is a stepping stone to the second.

### 3.1 Operator-declared public client (cheap, ships first)

Add `Lakehold:Oidc:McpClientId` (and optional `McpScopes`). The operator registers one public,
PKCE-only client in their IdP with the loopback redirect URIs MCP clients use, and LakeHold
advertises it so a client skips registration entirely.

Cost: one options property, one PRM field, and a section in
[`IDENTITY-PROVIDER-SETUP.md`](IDENTITY-PROVIDER-SETUP.md) with the per-provider registration steps.
It works with a locked-down IdP, which is the deployment LakeHold is built for.

Limit: the operator does IdP work before an agent can connect, and token lifetime is the IdP's.

### 3.2 LakeHold as the authorization server (better, larger)

LakeHold fronts the IdP: it accepts DCR from any MCP client, runs the authorization-code flow, hands
the user off to the IdP for the actual authentication, and mints its own access and **refresh**
tokens bound to the resolved `TenantMember`.

This is what makes `claude mcp add` a login rather than a configuration exercise, and it is the only
way to control session lifetime for an agent that runs for days — the browser cookie is eight hours
and a typical IdP access token is an hour, neither of which is an agent session.

It is also materially more surface: an authorization endpoint, a token endpoint, a registration
endpoint, consent, refresh rotation, and revocation that composes with the existing token revocation
so one action closes both. Do not start here. Ship 3.1, then decide whether the UX gap justifies it.

**Either way:** `PublicBaseUrl` in System Settings becomes load-bearing rather than advisory, since
the redirect and resource values must be the address the *client* uses. `McpOptions.PublicBaseUrl`
already carries that reasoning; the setup documentation should stop treating it as optional for any
deployment behind a proxy.

---

## Phase 4 — the operator tool tier

`execute` already gives complete control of the *data*. What has no MCP tool at all is the control
plane. Measured against the routes in `src/Lakehold.Api/Endpoints/`:

| Area | Routes with no tool | Capability |
|---|---|---|
| Provisioning | create/delete tenant, create/delete catalog | `Instance` |
| Credentials | create/list/revoke tokens | `TenantAdmin` |
| Users | list/update/remove members | `TenantAdmin` |
| Saved queries | CRUD, execute, publish/unpublish | `TenantWrite` |
| Maintenance | flush, compact, expire, cleanup, backup, schedule | `TenantOwner` |
| Recovery | list backups, restore, `restore-table` | `TenantOwner` / `TenantWrite` |
| Exit | eject, list ejects | `TenantOwner` |
| CDC | subscriptions CRUD, consumer registration | `TenantWrite` |
| Import | CSV and tabular upload | `TenantWrite` |
| Physical layer | storage, storage/files, table-detail, table-profile, column-distribution | `TenantData` |
| Instance | system settings read/save, storage config, path resolve | `Instance` |
| Audit | query history | `TenantData` |

Managed connectors are already covered, both halves.

### The gate is a tier, not a second switch

`McpExtensions` gates writes on each tool's own `ReadOnlyHint` annotation rather than a list of names,
in both the list-tools and call-tool filters — which is what stopped the connector tools shipping
reachable when writes were off. Extend that mechanism rather than adding a parallel one: a third
runtime setting, **Allow operator commands**, registering the maintenance, recovery, import, saved
query, CDC, and physical-layer tools, off by default, saved in System Settings beside Enabled and
Allow writes. Discovery and enforcement must read the same annotation, exactly as they do now.

Sequence within the phase, easiest and safest first:

1. **Read-only physical layer and history** — storage, table-detail, table-profile,
   column-distribution, query history. `TenantData`, annotated read-only, no new gate needed. These
   are the highest-value-per-risk tools on the list: an agent that can see file counts and delete
   overhead can *advise* on maintenance without being able to run it.
2. **Saved queries** — ordinary `TenantWrite` CRUD, behind the existing write gate.
3. **Import** — `TenantWrite`. Note the surface takes an upload; an MCP tool needs a path or an
   inline-content contract instead, which is a design decision, not a projection.
4. **Maintenance and recovery, behind the new operator tier** — and **two-step, not one**. Invariant
   10 keeps destructive maintenance dry-run by default and invariant 12 keeps restore from
   overwriting; neither survives a one-shot tool call. Model them as `plan_maintenance` /
   `apply_maintenance`, where apply requires the plan's current snapshot id so an intervening commit
   forces a fresh review — the pattern `restore-table` already establishes (invariant 22).
5. **CDC subscriptions** — last, and only with care. Creating a subscription is an outbound side
   effect with a stored secret (invariant 17). Accept a secret *reference* only, exactly as the
   connector tools do.

### What stays withheld, and why that is not a gap

- **Eject.** Eject is the exit attestation. An agent minting a signed artifact asserting the lakehouse
  is exportable inverts the point of the artifact (invariants 16, 17).
- **Token minting.** A credential that can mint credentials is the one thing an agent must not reach.
- **Tenant and catalog provisioning.** `Instance` capability. If it is ever exposed it belongs behind
  its own tier, not the operator one.

None of these three is needed to control the lakehouse, and each has a specific reason recorded in
`MCP.md` that this document does not overturn.

### Long-running operations

Backup, restore, and eject are long-running. When any is exposed, it goes through the SDK 2.0 **Tasks**
extension, and Tasks must be reconciled with [`PUBLIC-API.md`](PUBLIC-API.md)'s `202 Accepted` +
`operationId` job model rather than invented twice. That reconciliation is a design task in its own
right and blocks item 4 above.

---

## Documentation obligations

- **Complete:** `MCP.md` records the actor/origin model, full tool inventory, runtime gates, OAuth and
  API-token flows, local smoke test, and deferred contracts.
- **Complete:** `AUTHENTICATION.md` and `IDENTITY-PROVIDER-SETUP.md` record mandatory audience
  validation, durable membership, actor/origin audit, provider registration, and exact Codex and
  Claude Code commands.
- **Complete:** `web/lakehold-ui/src/app/docs.content.md`, `README.md`, and the bundled Keycloak guide
  carry the local login path and point to `MCP.md` for the full reference.
- **Complete:** `ARCHITECTURE.md` and `AGENT.md` now describe the read, write, and operator-gated
  surface instead of the original five-tool read-only server.

## Open questions

- Whether 3.2 is worth building, or whether 3.1 plus a documented IdP registration is where this
  stops. Decide after 3.1 is in someone's hands.
- Whether the operator tier is one setting or several. One is simpler; several let an operator grant
  maintenance without granting CDC. Start with one and split only on a real request.
- What an import tool's content contract is — a server-side path is simplest and is also a path
  traversal surface, so it needs the same treatment catalog data paths get.
