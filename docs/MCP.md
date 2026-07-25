# The MCP server

The plan for serving the Model Context Protocol from `Lakehold.Api`, so an AI agent — Claude, ChatGPT,
Copilot, or a custom one — can explore a tenant's catalog and run SQL against it under a credential
that already means something.

Like [`AUTHENTICATION.md`](AUTHENTICATION.md) and [`PUBLIC-API.md`](PUBLIC-API.md), this is a
specification and a running record. It is written to be worked one step at a time: each step is
independently shippable and testable and leaves the product working. Nothing here contradicts an
invariant in `AGENT.md`; where a rule already exists, this document says how the MCP surface preserves
it rather than restating why.

**Status: Phase 1 has landed; the MCP surface itself is not built.** The capability rules now live in
one transport-neutral policy, which is the prerequisite everything else rests on. Nothing yet depends
on the MCP SDK, and the reason is timing — see [Version and timing](#version-and-timing).

## Why this, and why now

Every competitor shipped an MCP server during 2026 — MotherDuck's remote server (which is how Flights
and Dives are driven), Dremio's open-source one, Databricks and Snowflake alongside their semantic
layers. [`COMPETITIVE-RESEARCH.md`](COMPETITIVE-RESEARCH.md) records the convergence: agents as the
primary compute unit, MCP as the protocol layer beneath them. On that axis this is parity, and parity
arriving late.

The differentiator is not that Lakehold has an MCP server. It is *how it refuses*.

### Capability is attachment — restated for agents

Every competing MCP server enforces agent safety in a policy layer above the SQL: the agent asks for
something, a guard inspects the request, and the guard decides. That guard is inspecting text a
language model generated, which is the losing game invariant 4 exists to avoid.

Lakehold does not have to play it:

> An MCP tool declares a `Capability` exactly as a route does, and the same policy that guards
> the HTTP API enforces it. A read-only agent credential produces a read-only **attachment**, so an
> agent that writes fails **in DuckDB**, not in a check that a cleverly generated `INSERT` might route
> around. `DucklingPool` already keys sessions by catalog *and attachment mode* (invariant 20), so an
> agent's session can never be handed the writable handle a human's session opened.

That is invariants 4, 19, and 20 doing exactly what they were built for, on a surface that did not
exist when they were written. It is the one claim in this market that is not a prompt-engineering
promise, and it is the sentence the documentation should lead with.

## Where it lives

`src/Lakehold.Api/Mcp/`, following the precedent `PgWire/` already sets: a protocol surface inside the
API project rather than a project of its own. One process, one credential store, one audit trail, one
telemetry pipeline. A separate project would have to re-import the control plane and the engine to say
anything, and would duplicate the authorization seam this document spends most of its length
protecting.

## Tooling decision

**`ModelContextProtocol.AspNetCore`** — the C# SDK co-maintained by Microsoft and Anthropic — is the
dependency. It hosts an MCP server as an ordinary ASP.NET Core endpoint (`MapMcp()`) reusing the
existing DI container, authentication, logging, and OpenTelemetry wiring.

**Microsoft Agent Framework (`Microsoft.Agents.AI`) is deliberately *not* used**, and the distinction
is worth recording because it is easy to get backwards. Agent Framework builds *agents*: orchestration,
chat loops, tool selection, and **consuming** MCP servers. Lakehold's job here is the inverse — to
**be** the server an agent connects to. The two are complementary layers, not competing choices.

Agent Framework becomes the right tool for exactly one future feature: an in-product assistant in the
Angular workbench, which would be an agent, and which would consume Lakehold's own MCP server like any
other client. That is a UI product and a separate decision. It is out of scope here.

## Version and timing

| | |
|---|---|
| MCP specification revision **2026-07-28** | final |
| C# SDK **2.0.0-rc.1** | published 25 July 2026 |
| SDK **2.0.0** stable | committed by the maintainers "on or before 2026-07-28" |

The 2026-07-28 revision is not a point release. Per secondary reporting — **not yet verified against
the specification text, and the first task of Phase 1 is to verify it** — it removes sessions, drops
the initialization handshake, deprecates three core features, rewrites authorization around OAuth 2.1
resource servers, and adds an extensions framework.

Two consequences, both decided:

1. **Nothing is written against SDK 1.4.x.** It would be legacy within the week.
2. **Nothing takes a dependency on 2.0.0-rc.1 either.** Release-candidate analyzer diagnostics (the rc
   notes cite `MCP9007`) fail a build with warnings-as-errors enabled centrally, which this repository
   does. Design and tests are written first; the package reference is added when 2.0.0 stable ships.

## The structural problem, and the refactor it forced — landed

This is the one place where MCP does not map cleanly onto the existing model, and Phase 1 existed to
solve it. It has shipped; what follows describes the problem and then what was actually built.

`LakeholdAuthorizationFilter` is an `IEndpointFilter`. It reads the required capability from
**endpoint metadata** (`RequireCapability(…)`) and the tenant and catalog from **route values**
(`{tenantSlug}`, `{catalogName}`). Both assumptions hold for `/api/tenants/{tenantSlug}/…` and neither
holds for MCP:

- There is **one HTTP endpoint** (`/mcp`), so endpoint metadata can declare only one capability for
  every tool the server exposes.
- The tenant and catalog arrive as **tool arguments** in a JSON-RPC body, not as route values.

The wrong fix is a second copy of the rules inside the MCP dispatch. That would put the 404-not-403
reasoning (invariant 19) in two places and guarantee they drift.

The fix is the one already applied to `TenantAccessPolicy`, which is transport-neutral and lives in
`Lakehold.ControlPlane.Security`: `LakeholdAuthorizationFilter.Enforce` was lifted into
**`CapabilityPolicy`**, taking a principal, a capability, a tenant, and a catalog, and returning a
decision rather than an `IResult`. `Capability` moved alongside it, because capability is a
property of the credential and the control plane owns the credential model.

The filter now maps that decision onto `Results.NotFound` / `Results.Problem` and contributes nothing
else to authorization. An MCP dispatch will map the same decision onto an MCP tool error. One set of
rules, two transports — which is what invariant 19 says in the first place.

Two details of the built version differ from the sketch this document originally carried, and both are
deliberate:

**The decision carries a reason.** A bare three-valued enum could not preserve the three distinct 403
messages the filter already returned, so `CapabilityDecision` is a `readonly record struct` of an
outcome plus an optional detail. `NotFound` carries **no** detail, on purpose: it exists to avoid
confirming that a tenant exists, and a reason attached to it would confirm exactly that. The zero value
of `CapabilityOutcome` is `NotFound`, so a `default`-constructed decision refuses rather than allows —
a decision type that fails open eventually fails open in production.

**`RouteCapability` was moved first and renamed to `Capability` second, in two commits.** The order
was the point. Both test files already import `Lakehold.Api.Auth` *and* `Lakehold.ControlPlane.Security`,
so moving the type between those namespaces let `LakeholdAuthorizationFilterTests`, `TokenRoleTests`,
and `TenantAccessPolicyTests` compile and pass **completely untouched** — which is what proves the
refactor changed no behaviour. Renaming in the same commit would have edited the very tests that
constitute that evidence. With the move proven, the rename is a mechanical, compiler-verified change
that touches those files harmlessly.

`RouteCapabilityMetadata` keeps its name. It is endpoint metadata by which an HTTP *route* declares
its `Capability`, so "Route" there is accurate rather than vestigial — and it is exactly the part an
MCP tool will not use, because a tool declares its capability without an endpoint to hang it on.

### What proves it

- `LakeholdAuthorizationFilterTests` (13), `TokenRoleTests`, and `TenantAccessPolicyTests` pass with
  **no edits** — the refactor is invisible to the HTTP surface.
- `CapabilityPolicyTests` is new and exercises the rules directly rather than through a transport:
  the fail-closed default, that `NotFound` explains nothing while `Forbidden` says why, that
  **subject is checked before capability** (a reader reaching another tenant gets `NotFound`, not
  `Forbidden`), and each capability's admission rules. This is the coverage a second transport needs
  in order to depend on the policy without re-deriving what a refusal means.

## Authentication

**The MCP endpoint always requires a credential, even when `Lakehold:Auth:RequireAuthentication` is
false.** This is a deliberate divergence from every other surface and it is not negotiable.
`ARCHITECTURE.md` already anticipates it: a new externally reachable surface must not assume every
request arrives authenticated, and must either require its own credential or ship with guidance to
enable enforcement. For a surface whose entire purpose is letting an autonomous agent execute SQL,
"trusts the route by default" is not defensible. A token-less MCP call is refused, full stop.

Two credential schemes are accepted, and they coexist — the same shape `PgWire` settled on:

### Lakehold API tokens

An `lkh_`-prefixed token presented as `Authorization: Bearer`. This is what works today with Claude
Code and any client that can set a header, it reuses `ApiTokenAuthenticator` unchanged, and revoking
the token closes the agent's access and the API's together. The token names the tenant; a tool
argument naming a different tenant is a **404**, never a 403 (invariant 19).

### OIDC, as an OAuth 2.1 resource server

The 2026 specification requires an MCP server to be a formal OAuth 2.1 resource server publishing
**RFC 9728 protected resource metadata** at `/.well-known/oauth-protected-resource`, with
`WWW-Authenticate` challenges pointing clients at the authorization server. The SDK ships
`McpAuthenticationHandler` for the server half.

When `Lakehold:Auth:Oidc` is configured, Lakehold serves that metadata pointing at the configured
issuer, and an agent presenting a JWT resolves to an `OidcPrincipal` through the existing path. When
OIDC is not configured, the metadata document is not served and token authentication is the only
route in — a deployment that has not configured an identity provider is not made to.

## Tool surface

Every tool below already exists as an endpoint. The MCP layer is a projection, not a second
implementation: each tool enters the engine through the same seam its HTTP route does, exactly as the
wire endpoint does.

| Tool | Capability | Notes |
|---|---|---|
| `list_tenants` | `Listing` | Scoped to the principal — an instance credential sees every tenant, a tenant credential sees its own |
| `list_catalogs` | `Listing` | As above |
| `describe_schema` | `TenantData` | Schemas, tables, columns. **Must filter `ducklake_*` internals** — verified behaviours 2 and 9 in `ARCHITECTURE.md`, or an agent sees ~28 metadata tables per tenant and reasons about them |
| `query` | `TenantData` | Read-only in v1; see below. A materialising path, so a row cap applies (invariant 6) |
| `list_snapshots` | `TenantData` | Time travel is shipped here and is *not* shipped by the closest peer. "What did this table look like on Tuesday" is a natural agent question and a differentiated answer |
| `list_changes` | `TenantData` | The CDC feed, paged. "What changed since snapshot N" is the other natural one. Windows are inclusive at both ends (invariant 18) — the tool must not re-expose that trap to a caller passing arbitrary bounds |

**Resources** carry a catalog's schema, so a client can attach it as context without spending a tool
call. **Prompts** are not shipped in v1 — there is no workflow yet whose shape is worth freezing into
the protocol.

**Transport** is Streamable HTTP. Given the 2026-07-28 revision removes sessions, there is no
server-side session state to design. A stdio shim is not shipped: Lakehold is a server, and remote MCP
is what its clients speak.

### Read-only in v1

The `query` tool attaches the catalog **read-only regardless of the credential's capability**. A
credential that may write over HTTP still cannot write through MCP.

This is a stronger rule than "the token decides", and it is chosen deliberately. It means the v1 blast
radius is exactly zero mutations, it means the read-only-attachment claim above is unconditionally
true rather than conditionally true, and it costs nothing that cannot be added later. Whether writes
are ever enabled — behind an explicit `Lakehold:Mcp:AllowWrites` *and* a read-write credential, both
required — is a Phase 4 decision to be taken on evidence, not now.

### What is deliberately unimplemented

The section that makes this document useful, in the same spirit as `POSTGRES-WIRE.md`.

| Not exposed | Why |
|---|---|
| Maintenance — `expire`, `cleanup`, `compact`, `flush` | Destructive operations are dry-run by default with an explicit apply path (invariant 10). That two-step contract does not survive translation into a one-shot tool call, and `TenantOwner` is not a capability to hand an agent by default |
| Eject | Eject *is* the exit attestation. An agent minting a signed artifact that asserts the lakehouse is exportable inverts the point of the artifact (invariants 16, 17) |
| Backup and restore | Long-running and destructive-adjacent; restore's refusal to overwrite (invariant 12) is a safety property that deserves a human |
| Provisioning — create/delete tenants and catalogs | `Instance` capability. An MCP server that can create tenants is a liability, not a feature |
| Token minting | `TenantAdmin`. A credential that can mint credentials is the one thing an agent must never reach |
| CDC subscriptions | Creating a webhook subscription is an outbound side effect with a stored secret (invariant 17) |
| Arbitrary DDL | Follows from read-only |

Long-running operations, if any of the above are ever exposed, go through the SDK 2.0 **Tasks**
extension rather than a bespoke mechanism — and Tasks is structurally the same thing as
`PUBLIC-API.md`'s `202 Accepted` + `operationId` job model. Those two designs must be reconciled, not
invented twice.

## The context budget is a separate budget

`Lakehold:Mcp:MaxRowsPerResult`, defaulting **well below** `LakehouseOptions.MaxRowsPerResult`.

Invariant 6 says the cap belongs to paths that materialise a result, and the MCP `query` tool
materialises one — so a cap applies and this is not a new rule. What *is* new is that the number
should differ. The HTTP cap bounds a JSON response built in memory; the MCP cap bounds a language
model's context window, and an agent that asks for a million rows is not doing anything unusual. Two
purposes, two numbers, one invariant.

The tool's response states when it truncated, so an agent can narrow its query rather than silently
reasoning about a prefix of the data.

## Audit

Every statement executed through MCP is recorded in query history against the resolved principal,
exactly as an HTTP query is. The surface additionally records that the caller was an MCP client, so
an operator can answer "what has the agent been running" as a first-class question rather than by
inference. Submitted SQL is already recorded by the existing audit path; nothing about agent-authored
SQL changes what may be logged, and the prohibition on logging credentials is unchanged.

## Phases

Each leaves the product working and is independently testable.

**Phase 1 — the seam. Landed.** `Enforce` lifted into `CapabilityPolicy`, HTTP behaviour proven
unchanged by the existing filter tests, new direct coverage of the rules. No MCP dependency. Still
outstanding from this phase: **verify the 2026-07-28 specification text** against the assumptions
above, which needs the published spec rather than the reporting summarising it.

**Phase 2 — the server.** Take `ModelContextProtocol.AspNetCore` 2.0.0 stable. Host the endpoint,
wire authentication (both schemes), serve protected-resource metadata, and expose exactly one tool:
`query`, read-only. Full test suite below.

**Phase 3 — discovery.** `list_tenants`, `list_catalogs`, `describe_schema`, and the schema resource.

**Phase 4 — the differentiated tools.** `list_snapshots` and `list_changes` — time travel and CDC, the
two capabilities the competitive research says are genuinely ahead.

**Phase 5 — decisions deferred to evidence.** Writes behind explicit configuration; Tasks-based
long-running operations; the in-product assistant, which is where Agent Framework returns.

## Test plan

`tests/Lakehold.Api.Tests/`, following the existing `PgWire*` family's shape — a protocol surface gets
protocol-level tests, not just unit tests of its helpers.

**Protocol conformance** (`McpProtocolTests`)
- A round trip driven by the **SDK's own client** against `TestServer`: connect, `tools/list`,
  `tools/call`. Hand-rolled JSON-RPC assertions would test the fixture rather than our conformance.
- Every advertised tool has a schema a client can call without guessing.
- Protected-resource metadata is served when OIDC is configured, and absent when it is not.
- The `WWW-Authenticate` challenge names the authorization server.

**Authorization** (`McpAuthorizationTests`) — the heart of the suite
- A token-less call is refused **while `RequireAuthentication` is false**. This is the divergence above
  and it must be pinned, because the surrounding default pulls the other way.
- A read-only credential cannot write, **and the refusal comes from the engine** — the assertion
  `ReadOnlyAttachmentTests` already makes, restated over MCP.
- With writes disabled (v1), a *read-write* credential still cannot write.
- A tool argument naming an unreachable tenant returns an error that does not disclose whether it
  exists — invariant 19's 404-not-403 reasoning, translated into MCP's error shape.
- An instance credential cannot query; a catalog-narrowed credential cannot reach another catalog.
- A tool's declared capability is enforced by the *same* policy the HTTP route uses — asserted by
  driving both transports through one table of cases, so drift fails a test.

**Behaviour** (`McpToolTests`)
- The MCP row cap is applied and is **distinct** from the HTTP cap; truncation is reported.
- `describe_schema` omits `ducklake_*` internals and the inlined-data tables.
- `list_changes` handles the inclusive-both-ends window correctly and refuses a range whose end
  predates the table (verified behaviours 6 and 7).
- Cancellation propagates from the transport through the engine.
- A statement run through MCP appears in query history against the right principal, marked as
  MCP-originated.

**Unchanged, and that is the point** — `LakeholdAuthorizationFilterTests`, `TenantAccessPolicyTests`,
and `TokenRoleTests` must pass untouched after Phase 1. If the refactor needs them edited, it changed
behaviour and is wrong.

## Documentation obligations

Shipping this is not done until:

- This document records what landed, as `AUTHENTICATION.md` does.
- `AGENT.md` carries the invariant (a tool declares a capability; the surface always requires a
  credential) and the repository-map entry.
- `ARCHITECTURE.md`'s matrix moves the AI / MCP row to ✅ and the roadmap moves it out of Next.
- `web/lakehold-ui/src/app/docs.content.md` gains a section — it is the single source for the in-app
  page and the GitHub guide, so there is one place to edit, not two.
- `README.md` shows the connection snippet an agent client needs.

## Open questions

- **The specification text is unverified.** Everything above about sessions, the handshake, and the
  authorization rewrite comes from secondary reporting. Phase 1 verifies it directly.
- **.NET 10 target support** in SDK 2.0.0 is assumed and unconfirmed.
- ~~Whether `RouteCapability` is renamed.~~ Settled: moved in Phase 1, renamed to `Capability`
  immediately after, for the reason given above.
- **Whether the MCP endpoint is separately toggleable** (`Lakehold:Mcp:Enabled`) or on whenever
  authentication is configured. Defaulting a new agent-reachable surface to *on* deserves an argument
  before it is made.
