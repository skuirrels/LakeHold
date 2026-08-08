# The MCP server

`Lakehold.Api` exposes a Model Context Protocol (MCP) server that lets AI agents — Claude, ChatGPT,
Copilot, or custom clients — explore a tenant's catalog and run SQL using the same credentials and
capability rules as the rest of LakeHold.

Like [`AUTHENTICATION.md`](AUTHENTICATION.md) and [`PUBLIC-API.md`](PUBLIC-API.md), this is a
specification and a running record. It is written to be worked one step at a time: each step is
independently shippable and testable and leaves the product working. Nothing here contradicts an
invariant in `AGENT.md`; where a rule already exists, this document says how the MCP surface preserves
it rather than restating why.

**Status: Phases 1-5 have landed, bar two items that are blocked or have nothing to carry.** The
capability rules live in one transport-neutral policy; the endpoint serves twelve read-only tools —
`list_tenants`, `describe_schema`, `query`, `list_snapshots`, `get_snapshot`, `query_snapshot`,
`list_changes`, and the connector control plane's read half (`list_connectors`, `get_connector`,
`validate_connector`, `list_connector_runs`, `list_connector_dead_letters`) — plus schema and snapshot
resources, behind a credential it always demands; it publishes RFC 9728 protected-resource metadata
where OIDC is configured; and eight mutating tools — `execute` plus `create_connector`,
`update_connector`, `retire_connector`, `run_connector`, `retry_connector`, `pause_connector`, and
`resume_connector` — appear only where an operator has opted into writes. What remains is recorded
under [Phases](#phases) with the reason rather than as an aspiration.

It is enabled for `make dev` and the development Compose stack. Production configuration remains
closed before first use. An instance operator can then change Enabled, Allow writes, maximum rows,
and the public base URL under **Workbench → System Settings**; the shared PostgreSQL row takes effect
on the next request across every API node, without a restart.

## Why this, and why now

Every competitor shipped an MCP server during 2026 — MotherDuck's remote server (which is how Flights
and Dives are driven), Dremio's open-source one, Databricks and Snowflake alongside their semantic
layers. [`COMPETITIVE-RESEARCH.md`](COMPETITIVE-RESEARCH.md) records the convergence: agents as the
primary compute unit, MCP as the protocol layer beneath them. On that axis this is parity, and parity
arriving late.

The differentiator is not that LakeHold has an MCP server. It is *how it refuses*.

### Capability is attachment — restated for agents

Every competing MCP server enforces agent safety in a policy layer above the SQL: the agent asks for
something, a guard inspects the request, and the guard decides. That guard is inspecting text a
language model generated, which is the losing game invariant 4 exists to avoid.

LakeHold does not have to play it:

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
chat loops, tool selection, and **consuming** MCP servers. LakeHold's job here is the inverse — to
**be** the server an agent connects to. The two are complementary layers, not competing choices.

Agent Framework becomes the right tool for exactly one future feature: an in-product assistant in the
Angular workbench, which would be an agent, and which would consume LakeHold's own MCP server like any
other client. That is a UI product and a separate decision. It is out of scope here.

## Version and timing

| | |
|---|---|
| MCP specification revision **2026-07-28** | final |
| C# SDK **2.0.0-rc.1** | published 25 July 2026 |
| SDK **2.0.0** stable | published; taken 3 August 2026 |

The 2026-07-28 revision is not a point release. Per secondary reporting — **still not verified against
the specification text** — it removes sessions, drops the initialization handshake, deprecates three
core features, rewrites authorization around OAuth 2.1 resource servers, and adds an extensions
framework.

The decision taken:

1. **Nothing is written against SDK 1.4.x.** It tracks the superseded revision and would be legacy
   within the week.
2. **The dependency was `2.0.0-rc.1`**, taken deliberately on the basis that nothing here ships as
   stable before the SDK does. The earlier concern that release-candidate analyzer diagnostics (the rc
   notes cite `MCP9007`) would break a warnings-as-errors build did **not** materialise — the package
   restores and builds with zero warnings, and `MCP9007` applies to a client-side OAuth API this
   server does not use. **Settled 3 August 2026:** 2.0.0 stable published and was taken; the build
   and the full backend suite pass on it.

The package reference carries this reasoning as a comment in `Directory.Packages.props`, because the
version history is otherwise unexplained to a later reader.

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
was the point. Both test files already import `Lakehold.Api.Auth` *and*
`Lakehold.ControlPlane.Security`, so moving the type between those namespaces let
`LakeholdAuthorizationFilterTests`, `TokenRoleTests`,
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

**The MCP endpoint always requires a named credential, and unlike every other surface it will not
accept the read-only demo identity.** This divergence is deliberate and it is not negotiable.
`ARCHITECTURE.md` already anticipates it: a new externally reachable surface must not assume every
request carries a *named* credential, and must require its own where that matters. For a surface
whose entire purpose is letting an autonomous agent execute SQL, "whoever can reach it" is not
defensible however narrowly the demo identity is scoped. A credential-less MCP call is refused, full
stop.

Two credential schemes are accepted, and they coexist — the same shape `PgWire` settled on:

### LakeHold API tokens

An `lkh_`-prefixed token presented as `Authorization: Bearer`. This is what works today with Claude
Code and any client that can set a header, it reuses `ApiTokenAuthenticator` unchanged, and revoking
the token closes the agent's access and the API's together. The token names the tenant; a tool
argument naming a different tenant is a **404**, never a 403 (invariant 19).

### OIDC, as an OAuth 2.1 resource server — shipped

The 2026 specification requires an MCP server to be a formal OAuth 2.1 resource server publishing
**RFC 9728 protected resource metadata** at `/.well-known/oauth-protected-resource`, with
`WWW-Authenticate` challenges pointing clients at the authorization server. Both halves are served.

The document names the MCP endpoint as the `resource`, the configured issuer under
`authorization_servers`, and `header` as the only supported bearer method — LakeHold reads a credential
from the `Authorization` header and nowhere else, never a query parameter, which would put it in access
logs and referrers. It is **unauthenticated by design**: a client reads it *because* it has no
credential yet, so requiring one would be circular, and it discloses only an issuer and a resource,
both already public, and no tenant. An unauthenticated MCP call is answered
`401` with `WWW-Authenticate: Bearer resource_metadata="…"`, which is the thread a client follows.

**Served only where OIDC is configured.** Absent an authority there is no authorization server to name,
and a document advertising none is worse than none: a client discovers it, learns nothing, and fails
somewhere less obvious. There the challenge stays a bare `Bearer` and API tokens are the only
credential.

**Set Public base URL in System Settings behind a reverse proxy.** The metadata advertises a `resource` and
the challenge cites the document's own URL; a client compares the first against the URL it called and
follows the second, so both must be the address the *client* uses. In the documented production
topology the API runs unpublished behind nginx, where `Request.Scheme` and `Request.Host` describe the
internal hop — inferring from the request would advertise a host no client can resolve. Trusting
`X-Forwarded-*` instead would mean trusting headers any caller can set unless the proxy list is pinned,
so this is declared rather than sniffed. Left empty the request is used, which is right for a directly
exposed API and for local development.

It is written as an ordinary endpoint rather than by registering the SDK's `McpAuthenticationHandler`,
and that is a deliberate choice. The handler is an ASP.NET Core authentication *scheme* that serves the
document and the challenge from the middleware's challenge path — but LakeHold does not authenticate
through that pipeline. Identity is resolved by an endpoint filter, and a second scheme overlapping that
filter would be two things deciding one question. The document is the contract, not the mechanism, so
it is emitted directly and serialised through the SDK's own `ProtectedResourceMetadata` type, which
keeps the field names the spec's rather than ours.

## Tool surface

Every tool below already exists as an endpoint. The MCP layer is a projection, not a second
implementation: each tool enters the engine through the same seam its HTTP route does, exactly as the
wire endpoint does.

| Tool | Capability | Notes |
|---|---|---|
| `list_tenants` | `Listing` | **Shipped.** Tenants *and* their catalogs, scoped to the principal. A separate `list_catalogs` was specified and then dropped: catalogs come back nested here, so a second tool would answer a question already answered and cost the agent context to read. Stricter than the HTTP listing route in one way — a catalog-narrowed credential sees only its own catalog, because naming one the caller cannot query wastes its next call |
| `describe_schema` | `TenantData` | **Shipped.** Schemas, tables, columns. `ducklake_*` internals are filtered by `CatalogBrowser` at the source — verified behaviours 2 and 9 in `ARCHITECTURE.md` — which matters more here than in the workbench: a human scrolls past ~28 internal tables, an agent reasons about them |
| `query` | `TenantData` | **Shipped.** Read-only; see below. A materialising path, so a row cap applies (invariant 6) |
| `list_snapshots` | `TenantData` | **Shipped.** Time travel, which the closest peer's own roadmap still lists as forthcoming. It also supplies the bounds `list_changes` takes |
| `get_snapshot` | `TenantData` | **Implemented in source.** Returns one retained snapshot by native id and refuses a missing id without inventing a second snapshot store |
| `query_snapshot` | `TenantData` | **Implemented in source.** Bounded table preview at an exact retained snapshot through the same structural read-only attachment as REST; it does not present a materialized MCP result as streaming |
| `list_changes` | `TenantData` | **Shipped.** The CDC feed, paged. Inclusive at both ends (invariant 18, verified behaviour 6), and the tool's *description* says so — an agent that assumes exclusivity skips a window. Omitting the upper bound reads to the newest snapshot, which is also what keeps a caller clear of verified behaviour 7, where a range ending before the table existed raises. The engine's complaint is forwarded verbatim when it does |

**Resources.** `lakehold://{tenant}/{catalog}/schema` carries the same information
`describe_schema` returns, so a client can pin it as standing context instead of spending a tool call
whenever it needs a column name. `lakehold://{tenant}/{catalog}/snapshots/{snapshotId}` carries the
same bounded metadata as `get_snapshot`. Both are **templates**, not concrete resource lists, and that is a
security choice rather than a convenience one: enumerating every reachable catalog would mean
resolving the credential during resource *listing*, and listing is the one place a mistake would hand
catalog names to a caller that cannot reach them. A template discloses nothing.

Both tools and resources authorise through one `McpCaller`. A resource that authorised differently
from a tool would be a hole shaped precisely like the one invariant 21 closes, so there is deliberately
no second path to get wrong.

**Prompts** are not shipped — there is no workflow yet whose shape is worth freezing into the
protocol.

**Transport** is Streamable HTTP. Given the 2026-07-28 revision removes sessions, there is no
server-side session state to design. A stdio shim is not shipped: LakeHold is a server, and remote MCP
is what its clients speak.

### Connecting a client

The endpoint speaks Streamable HTTP at `Lakehold:Mcp:Route` (default `/mcp`) and authenticates with
an ordinary LakeHold API token in an `Authorization: Bearer` header. Issue one scoped to what the
agent should reach — a catalog-narrowed, reader-role token is the right default, and it costs nothing
because the surface forces a read-only attachment anyway:

First sign in through the configured identity provider as a system administrator (or use the
break-glass instance credential), then enable MCP in **Workbench → System Settings**. A fresh
development stack already has it enabled. The settings page shows a copyable endpoint on the
Workbench origin; the development server proxies that path to the API, while direct access on
`http://localhost:5200/mcp` remains available. Then open **Users** and, in its **API tokens** card,
choose the workspace, narrow the credential to the catalog the agent needs, retain the reader default, and
generate it. Existing credentials and their last-use state are listed below; revoking one closes its
MCP and HTTP access. The plaintext is shown once. The equivalent public API call is:

```bash
curl -X POST https://lakehold.example.com/api/tenants/demo/tokens \
  -H "Authorization: Bearer $LAKEHOLD_ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name":"claude-agent","role":"reader","catalogName":"analytics"}'
```

Keep the token out of files that get committed. Every example below reads it from the environment.

#### Claude Code

```bash
claude mcp add --transport http lakehold https://lakehold.example.com/mcp --header "Authorization: Bearer $LAKEHOLD_TOKEN"
```

Or check a project-scoped server into `.mcp.json` — note that `type` is **required** alongside `url`,
because an entry with a `url` and no `type` is read as a stdio server and skipped:

```json
{
  "mcpServers": {
    "lakehold": {
      "type": "http",
      "url": "https://lakehold.example.com/mcp",
      "headers": { "Authorization": "Bearer ${LAKEHOLD_TOKEN}" }
    }
  }
}
```

Verify with `claude mcp list`, then ask Claude to run a query:

> Using the lakehold server, query tenant `demo`, catalog `analytics`: `SELECT count(*) FROM orders`

#### Codex

Codex reads `~/.codex/config.toml`. Point it at the same URL and let it take the token from the
environment rather than from the file:

```toml
[mcp_servers.lakehold]
url = "https://lakehold.example.com/mcp"
bearer_token_env_var = "LAKEHOLD_TOKEN"
startup_timeout_sec = 10.0
tool_timeout_sec = 60.0
```

`env_http_headers = { "Authorization" = "LAKEHOLD_AUTH_HEADER" }` is the alternative when a header
has to be sent verbatim, and `enabled_tools = ["query"]` pins the surface even if a later LakeHold
version adds tools.

#### Anything else

Any MCP client that speaks Streamable HTTP and can set a request header will connect; there is
nothing LakeHold-specific in the handshake. Over plain HTTP the token crosses the wire in the clear,
so terminate TLS in front of the API exactly as you would for the REST surface.

### First contact: the catalog must already exist

A read-only attachment cannot *create* a DuckLake metadata file. A catalog that has been provisioned
but never written to therefore fails to attach, and the agent sees an engine error about opening a
database in read-only mode rather than an empty catalog.

This is not MCP-specific — a read-only *token* on the HTTP route behaves the same way — but MCP hits
it far more often, because this surface is read-only always. Write to a catalog once (any statement
that creates a table will do) before pointing an agent at it. Worth revisiting if provisioning is ever
made to initialise the metadata file at creation time, which would remove the sharp edge entirely.

### Reads and writes are different tools

The `query` tool attaches the catalog **read-only regardless of the credential's capability**, and that
never changes — not even where writes are enabled. A credential that may write over HTTP cannot write
through `query`.

Writes are *separate* tools, exposed only when **Allow write commands** is saved in System Settings. The
reason is the tool annotations: MCP clients read `readOnly` and `destructive` to decide whether to ask
a human before calling, and those live in an attribute fixed at compile time. A `query` that sometimes
wrote would advertise itself read-only while writing, or destructive while doing nothing of the kind —
either way a client makes a safety decision on false information. Splitting them keeps every annotation
true, and buys a second property worth having: **the tool list itself says whether this deployment
permits writes**, visible to an operator or an agent that cannot read the configuration.

Two gates, not one. The operator opts in *and* the credential must not be read-only. A read-only
credential still produces a read-only attachment, so its refusal comes from DuckDB (invariants 4
and 20); the explicit check exists only so the agent reads "your credential cannot write" instead of an
engine error about the catalog.

**The gate is the annotation, not a list of names.** The list-tools filter removes every tool whose
`readOnly` hint is not true, and the call-tools filter resolves the same annotation for the requested
name — so discovery and enforcement cannot describe different surfaces, and a client calling from a
stale tool cache is refused rather than served. This matters because the first version of the gate
matched the literal name `execute`: when the connector control plane arrived, its seven mutating tools
were reachable on a deployment with writes turned off, and nothing in the design said so. Any tool
annotated `ReadOnly = false` is now covered the moment it is registered, and `McpWriteToolTests` asserts
over the whole advertised set rather than over a list somebody has to remember to extend.

### The connector control plane

`list_connectors`, `get_connector`, `validate_connector`, `list_connector_runs`, and
`list_connector_dead_letters` are read-only. `create_connector`, `update_connector`, `retire_connector`,
`run_connector`, `retry_connector`, `pause_connector`, and `resume_connector` mutate, and sit behind
**Allow write commands** with the rest.

They are here rather than withheld because a managed connector is ordinary durable catalog
configuration that an administrator already edits in the Workbench, and both surfaces call the *same*
validation boundary — `DataConnectorEndpoints.ValidateAsync` — so an agent cannot save a definition the
administrator UI would refuse. All eleven declare `Capability.TenantOwner`, the same capability the
HTTP routes declare, enforced by the same policy (invariant 21). Secret *references* are accepted;
secret values never are, on either surface.

`run_connector` and `retry_connector` are annotated **destructive**: a full-snapshot connector replaces
the whole of its DuckLake target, so a client should treat a run as an operation worth confirming
rather than an additive one.

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
| Arbitrary DDL | Available through `execute` where an operator has enabled writes, and nowhere else |
| Connector *secret values* | A connector tool accepts a secret reference and never a value, exactly as the HTTP surface does. The operator's binding decides which reference resolves at which destination (invariant 23) |

Long-running operations, if any of the above are ever exposed, go through the SDK 2.0 **Tasks**
extension rather than a bespoke mechanism — and Tasks is structurally the same thing as
`PUBLIC-API.md`'s `202 Accepted` + `operationId` job model. Those two designs must be reconciled, not
invented twice.

## The context budget is a separate budget

**Maximum rows per MCP result** in System Settings, defaulting **well below**
`LakehouseOptions.MaxRowsPerResult`.

Invariant 6 says the cap belongs to paths that materialise a result, and the MCP `query` tool
materialises one — so a cap applies and this is not a new rule. What *is* new is that the number
should differ. The HTTP cap bounds a JSON response built in memory; the MCP cap bounds a language
model's context window, and an agent that asks for a million rows is not doing anything unusual. Two
purposes, two numbers, one invariant.

The tool's response states when it truncated, so an agent can narrow its query rather than silently
reasoning about a prefix of the data.

**The ceiling applies to every list-shaped tool, not only `query`.** This was a review finding: a
change feed page defaults to 1000 and admits 10000 over HTTP, which are the right numbers for a
consumer writing to a database and the wrong ones for a context window. A tool that returned them would
defeat the budget this option exists to keep, so the runtime settings snapshot bounds `list_snapshots` and
`list_changes` by the same number, and a test asserts it for both.

## Audit

Every statement executed through MCP is recorded in query history against the resolved principal,
exactly as an HTTP query is — the tool passes the principal's token id down the same path the HTTP
route does. Submitted SQL is already recorded by the existing audit path; nothing about agent-authored
SQL changes what may be logged, and the prohibition on logging credentials is unchanged.

**There is no MCP-origin marker yet**, and an earlier draft of this document claimed there was. Today
an operator distinguishes agent traffic by *which token* ran the statement, which works because an
agent is issued its own credential — but it is a convention rather than a guarantee, and it fails the
moment a token is shared between an agent and a script. A first-class marker means a column on the
query-history record and a migration; it belongs to a later phase, and is listed there.

## Phases

Each leaves the product working and is independently testable.

**Phase 1 — the seam. Landed.** `Enforce` lifted into `CapabilityPolicy`, HTTP behaviour proven
unchanged by the existing filter tests, new direct coverage of the rules. No MCP dependency. Still
outstanding from this phase: **verify the 2026-07-28 specification text** against the assumptions
above, which needs the published spec rather than the reporting summarising it.

**Phase 2 — the server. Landed.** `ModelContextProtocol.AspNetCore` **2.0.0** (taken 3 August 2026;
landed originally on `2.0.0-rc.1` under the deliberate decision that nothing here ships as stable
before the SDK does) — see the note in `Directory.Packages.props`. The endpoint is hosted at `Lakehold:Mcp:Route`, guarded by
`McpAuthenticationFilter`, and exposes exactly one tool: `query`, read-only. Outstanding from this
phase: **protected-resource metadata (RFC 9728) is not served yet**, so OIDC-only clients that rely on
discovery cannot find the authorization server. Bearer tokens work today; that is the gap.

**Phase 3 — discovery. Landed.** `list_tenants`, `describe_schema`, and the schema resource. The
separately specified `list_catalogs` was dropped for the reason given in the tool table. An agent can
now start from `list_tenants` and work down to a query without being told anything in its prompt,
which is what turns the surface from a proven seam into something usable.

**Phase 4 — the differentiated tools. Landed.** `list_snapshots` and `list_changes` — time travel and
CDC, the two capabilities `COMPETITIVE-RESEARCH.md` says are genuinely ahead of the closest peer. Both
emit the same change vocabulary the REST feed and the webhooks use, so an agent and a webhook consumer
do not read two names for one event.

**Phase 5 — partly landed, partly blocked, and one item with nothing to carry.**

- **Writes behind an explicit runtime setting — landed.** Allow writes plus a read-write
  credential, as a separate `execute` tool for the annotation reason above.
- **RFC 9728 protected-resource metadata — landed.** See the authentication section.
- **An MCP-origin marker on query history — blocked, and not on anything MCP.** A run is attributed to
  its token id today, which identifies agent traffic only by the convention that an agent gets its own
  credential. A first-class marker means a column on `QueryRun`, and the control plane has no story for
  that: `AdditiveSchema` creates *missing tables* on start-up and says in its own remarks that columns
  added to an existing entity still need a real migration path. Adding one is a control-plane
  infrastructure change affecting every entity and every existing deployment — the right size of work,
  but not an MCP change, and it should not be smuggled in as one.
- **Tasks-based long-running operations — nothing to carry.** SDK 2.0's Tasks extension is the right
  mechanism for a long-running tool call, and this surface deliberately exposes no long-running
  operation: maintenance, eject, backup, and restore are all withheld for the reasons above. Building
  the mechanism before the operation would be speculative. When one is exposed, Tasks is how, and it
  must be reconciled with `PUBLIC-API.md`'s `202`/`operationId` model rather than invented twice.
- **The in-product assistant — a separate product.** An assistant in the Angular workbench is an agent
  that would consume this server like any other client. That is where Agent Framework returns, and it
  is a UI decision rather than a continuation of this document.

## Configuration and live settings

The file values below are bootstrap defaults only. They are used until an instance operator saves
System Settings. After that, the PostgreSQL singleton is authoritative for Enabled, PublicBaseUrl,
AllowWrites, and MaxRowsPerResult. `Route` remains a startup setting because changing the mapped URL
requires rebuilding the endpoint table.

```jsonc
// appsettings — all non-secret, so it lives in source control (the token does not)
"Lakehold": {
  "Mcp": {
    "Enabled": false,        // production bootstrap; Development overrides this to true
    "Route": "/mcp",
    "PublicBaseUrl": "",     // required behind a reverse proxy; see below
    "AllowWrites": false,    // bootstrap only; System Settings controls the live tool list
    "MaxRowsPerResult": 200  // bootstrap only; the UI accepts 1..10,000
  }
}
```

## Tests

`tests/Lakehold.Api.Tests/`, following the existing `PgWire*` family's shape — a protocol surface gets
protocol-level tests, not just unit tests of its helpers.

### Landed

**`McpAuthenticationFilterTests`** — the credential rule, exercised against the filter directly.
- A credential-less call is refused **even where demo access is configured**. This is invariant 21,
  and it is pinned precisely because the surrounding configuration pulls the other way.
- A valid token resolves and is stashed where the tools read it.
- Malformed, unknown, revoked, and expired credentials are each refused, with one opaque
  `WWW-Authenticate: Bearer` challenge that does not say which of those it was.

**`McpServerTests`** — the endpoint driven by the **SDK's own client** over a real HTTP transport, so
what is asserted is conformance rather than agreement with a hand-rolled fixture.
- `tools/list` returns `query` with a description a client can act on.
- `list_tenants` shows the caller's own tenant and catalogs, and does not name another tenant.
- `list_snapshots` returns the history newest-first and refuses another tenant without disclosing it.
- `list_changes` reports an insert, bounds the range it read, and is **inclusive at both ends** —
  asking from above the last change returns nothing, which is what proves a consumer resuming at
  `L + 1` replays rather than skips. An unknown table forwards the engine's own complaint.
- Every list-shaped tool honours the MCP page ceiling, asserted for changes and snapshots together.

**`McpWriteToolTests`** — the write gates, each on its own host, because `AllowWrites` decides what is
registered at start-up and so cannot vary within one server.
- `execute` is **absent** unless the operator enables it, so an upgrade does not silently acquire an
  agent that can mutate the lakehouse.
- **No** advertised tool is annotated non-read-only while writes are disabled. Asserted over the whole
  set rather than over named tools, so a mutating tool added later cannot land outside the gate — which
  is how the connector control plane first shipped reachable with writes off.
- Every mutating tool is refused **by name** while writes are disabled, covering a client that calls
  from a cached tool list rather than a fresh `tools/list`.
- `run_connector` advertises itself destructive and `list_connectors` read-only, so the annotation a
  client uses to decide whether to ask a human matches what the tool does.
- Enabling it advertises a tool annotated non-read-only and destructive, while `query` stays annotated
  read-only — the annotations a client trusts are asserted, not just the behaviour behind them.
- A read-write credential writes, proven by reading the table back rather than by the response.
- `query` still refuses a write where writes are enabled.
- A read-only credential is refused, and another tenant is refused without disclosure.
- A catalog-narrowed credential is shown only the catalog it can reach — the deliberate divergence
  from the HTTP listing route.
- `describe_schema` returns real columns and omits `ducklake_*` internals, and refuses another tenant
  without disclosing it.
- The schema resource is advertised as a **template** and the concrete resource list is empty, so
  listing discloses nothing; reading it returns the schema; and reading another tenant's is refused
  with the same wording a tool uses — the second authorization path cannot drift from the first.
- The exposed set is asserted **exactly**, so adding a tool is a decision rather than an accident.
- A client with no credential cannot connect at all.
- A tool call reaches the principal — which is what proves a tool can read the request's
  `HttpContext` and resolve its scoped dependencies from inside the SDK's dispatch. Both were real
  design risks; neither survived contact.
- A forbidden tenant and a genuinely missing catalog are **byte-identical** to the caller. The
  assertion is equality, because any difference would itself answer "does that tenant exist?"
  (invariant 19).
- The MCP row cap is applied, is the *MCP* number rather than the engine's, and reports truncation.
- **A write fails in the engine even for an owner credential** — the claim the surface rests on. The
  test asserts the refusal did not come from an attach failure, and then proves the table was not
  created rather than trusting the message.
- Columns carry their declared type.
- An empty statement is refused before the engine is touched.

**Unchanged, and that is the point** — `LakeholdAuthorizationFilterTests`, `TenantAccessPolicyTests`,
and `TokenRoleTests` passed untouched through Phase 1. `CapabilityPolicyTests` covers the shared rules
directly.

### Still owed

- ~~Protected-resource metadata.~~ Covered: `McpResourceMetadataTests` asserts the document's spec
  field names, that it needs no credential, that nothing is served without an authority, that the 401
  challenge cites it — and cites nothing where there is nothing to cite — and that a declared
  `PublicBaseUrl` overrides what the request saw.
- An instance credential cannot query. Holds by construction through `CapabilityPolicy`, which is
  covered — but not yet asserted *over MCP*.
- Cancellation propagating from the transport through the engine.
- Query-history attribution asserted end to end.
- Verified behaviour 7 asserted directly: a range whose *explicit* end predates the table's creation.
  The tools default the end to the newest snapshot, so the trap is only reachable by passing
  `toSnapshot` deliberately, and that path is forwarded but not yet covered.

## Documentation obligations

Shipping this is not done until:

- ~~This document records what landed.~~ Done, and it records the gaps too.
- ~~`AGENT.md` carries the invariant and the repository-map entry.~~ Done (invariant 21).
- `ARCHITECTURE.md`'s matrix moves the AI / MCP row to ✅ and the roadmap moves it out of Next.
  Currently ⚠️: the endpoint exists, the discovery tools do not.
- `web/lakehold-ui/src/app/docs.content.md` gains a section — it is the single source for the in-app
  page and the GitHub guide, so there is one place to edit, not two. **Outstanding**, and best written
  once Phase 3 makes the surface usable enough to recommend.
- `README.md` shows the connection snippet an agent client needs. **Outstanding**, same reason;
  [Connecting a client](#connecting-a-client) above is the source to copy from.

## Open questions

- **The specification text is still unverified.** Everything above about sessions, the handshake, and
  the authorization rewrite comes from secondary reporting. The SDK's behaviour is now exercised by
  tests, which is not the same as having read the spec.
- ~~.NET 10 target support in SDK 2.0.0.~~ Confirmed: the package ships a `net10.0` target, and the
  API builds and runs against it.
- ~~When to move off `2.0.0-rc.1`.~~ Settled 3 August 2026: 2.0.0 stable published and the
  dependency moved to it.
- ~~Whether `RouteCapability` is renamed.~~ Settled: moved in Phase 1, renamed to `Capability`
  immediately after, for the reason given above.
- ~~Whether the MCP endpoint is separately toggleable.~~ Settled: a shared runtime setting,
  bootstrapped by `Lakehold:Mcp:Enabled`. Production starts false; development starts true.
