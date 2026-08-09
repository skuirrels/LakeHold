# The MCP server

`Lakehold.Api` exposes a Model Context Protocol (MCP) server that lets AI agents — including Codex,
Claude Code, and custom clients — explore a tenant's catalog and run SQL using the same credentials
and capability rules as the rest of LakeHold.

Like [`AUTHENTICATION.md`](AUTHENTICATION.md) and [`PUBLIC-API.md`](PUBLIC-API.md), this is a
specification and a running record. It is written to be worked one step at a time: each step is
independently shippable and testable and leaves the product working. Nothing here contradicts an
invariant in `AGENT.md`; where a rule already exists, this document says how the MCP surface preserves
it rather than restating why.

**Status: the authenticated operator surface has landed, with the explicitly blocked contracts still
withheld.** In addition to catalog discovery, SQL, snapshots, CDC, and managed connectors, an agent can
inspect physical storage and query audit history, manage and execute saved queries, and run
snapshot-bound two-step maintenance. OAuth metadata is served at both RFC 9728 locations, includes
scopes and an optional pre-registered public client, and MCP JWTs are audience-bound to the exact
resource URL. Backup/restore Tasks, import content, eject, token minting, and instance provisioning
remain withheld for the reasons recorded below.

It is enabled for `make dev` and the development Compose stack. Production configuration remains
closed before first use. An instance operator can then change Enabled, Allow writes, Allow operator
commands, maximum rows, and the public base URL under **Workbench → System Settings**; the shared PostgreSQL row takes effect
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

## Version and protocol

| | |
|---|---|
| Transport | Streamable HTTP at `/mcp`; no stdio shim |
| C# SDK | `ModelContextProtocol.AspNetCore` **2.0.0** |
| OAuth discovery | RFC 9728 protected-resource metadata at both canonical locations |

The dependency first landed on `2.0.0-rc.1` while the surface was under development and moved to
stable `2.0.0` on 3 August 2026. The package restores and builds with warnings-as-errors, and the
server is exercised through the SDK's own client over a real HTTP transport.

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

An `lkh_`-prefixed token presented as `Authorization: Bearer`. Codex, Claude Code, and any client that
can set a header can use it. It reuses `ApiTokenAuthenticator` unchanged, and revoking the token
closes the agent's access and the API's together. The token names the tenant; a tool argument naming
a different tenant is a **404**, never a 403 (invariant 19).

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
| `get_storage`, `list_storage_files` | `TenantData` | **Shipped.** Read-only physical file counts and bounded file inventory, optionally at a snapshot |
| `get_table_detail`, `get_table_profile`, `get_column_distribution` | `TenantData` | **Shipped.** Logical/storage detail and bounded profiling through the same inspection services as HTTP |
| `query_history` | `TenantData` | **Shipped.** Catalog-scoped audit history including token/member actor and transport origin |
| `list_saved_queries`, `get_saved_query`, `execute_saved_query` | `TenantData` | **Shipped.** Reusable definitions and structurally read-only execution |
| `create_saved_query`, `update_saved_query`, `delete_saved_query`, `publish_saved_query`, `unpublish_saved_query` | `TenantWrite` | **Shipped behind Allow writes.** Uses optimistic revisions and the existing saved-query application service |
| `plan_maintenance`, `apply_maintenance` | `TenantOwner` | **Shipped behind Allow operator commands.** Apply requires the exact snapshot id returned by plan; an intervening commit forces review again. Backup is excluded until Tasks and public operations share one job model |

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

**Transport** is Streamable HTTP. LakeHold adds no application session state to MCP requests; tenant,
capability, and audit context resolve from the credential on each call. A stdio shim is not shipped:
LakeHold is a server, and remote MCP is what its clients speak.

### Connecting a client

The endpoint speaks Streamable HTTP at `Lakehold:Mcp:Route` (default `/mcp`). An interactive agent can
sign in through OAuth as its operator; unattended automation can use a LakeHold API token in an
`Authorization: Bearer` header.

First sign in through the configured identity provider as a system administrator (or use the
break-glass instance credential), then enable MCP in **Workbench → System Settings**. A fresh
development stack already has it enabled. The settings page shows a copyable endpoint on the
Workbench origin; the development server proxies that path to the API, while direct access on
`http://localhost:5200/mcp` remains available. Behind a reverse proxy, save the externally reachable
Workbench origin as **Public base URL** so OAuth metadata advertises the same MCP URL the client uses.

#### Local OAuth smoke test

Start the development stack:

```bash
make dev
```

Leave it running and use a second terminal for the client commands. If this development database has
saved System Settings from an earlier run, confirm **Enabled** is on and **Public base URL** is exactly
`http://localhost:5399` (the origin, without `/mcp`). Keep **Allow writes** and **Allow operator
commands** off for the first smoke test.

Use `http://localhost:5399/mcp`, not the container-only API address, for the easiest browser callback
and metadata path. The bundled Keycloak realm has a public PKCE client named `lakehold-mcp`. When the
browser opens, sign in as `analyst` with password `lakehold`; that identity owns the seeded `demo`
workspace. The `admin` user administers the instance but deliberately cannot query tenant data.

After connecting either client below, use this smoke-test prompt:

> Using the LakeHold MCP server, list the workspaces and catalogs I can reach. Describe the schema of
> tenant `demo`, catalog `analytics`, then run `SELECT 42 AS answer` there. Do not write anything.

If the catalog has never been initialized, create a table once from the Workbench before using a
reader: a read-only attachment cannot create the DuckLake metadata file.

#### Claude Code — OAuth as the signed-in person

Claude Code discovers LakeHold's authorization server from the RFC 9728 metadata. LakeHold uses a
pre-registered public client, so pass its id without a secret:

```bash
claude mcp add --transport http --client-id lakehold-mcp \
  lakehold http://localhost:5399/mcp
claude mcp login lakehold
claude mcp list
```

On Claude Code versions without `claude mcp login`, start `claude`, enter `/mcp`, select `lakehold`,
and complete the browser login. For production, replace the URL and client id with the values shown
by **System Settings** and registered at the identity provider. See Anthropic's
[official Claude Code MCP reference](https://code.claude.com/docs/en/mcp) for client configuration
and OAuth lifecycle details.

#### Codex — OAuth as the signed-in person

Codex needs the pre-registered public client id. LakeHold's protected-resource metadata supplies the
exact RFC 8707 resource URL:

```bash
codex mcp add lakehold \
  --url http://localhost:5399/mcp \
  --oauth-client-id lakehold-mcp
codex mcp login lakehold
codex mcp list
```

Keep `--oauth-client-id`: without it Codex attempts dynamic client registration, which many providers
(including the bundled Keycloak realm) deliberately disable. Do **not** also pass `--oauth-resource`.
Codex reads the resource from LakeHold's RFC 9728 metadata; an explicit copy makes it send two
identical `resource` parameters, and Keycloak refuses the request as
`invalid_request: duplicated parameter`.

On a development checkout upgraded from a version that predated `lakehold-mcp`, **Client not found**
means the already-created Keycloak container skipped the changed realm import. Recreate only that
development container, then repeat `codex mcp login lakehold`:

```bash
docker compose up -d --force-recreate --wait keycloak
```

This resets development-only Keycloak state; it does not remove LakeHold's PostgreSQL, catalog, or
MinIO data.

### Where a client looks for the authorization server

LakeHold is a resource server and publishes no authorization-server metadata of its own. RFC 9728 has
it name the issuer in its protected-resource document and has the client read that server's metadata
from the issuer. Clients do not all do this. At least one reads `authorization_servers`, then looks
for authorization-server metadata **only on the resource origin**, and on finding none falls back to
the pre-RFC-9728 MCP assumption that the MCP server is also its own authorization server — opening
`https://<lakehold>/authorize`, which has never existed. A 404 is the honest answer to those paths
and is exactly what triggers the guess.

Since 2.2.1, the four discovery paths — `oauth-authorization-server` and `openid-configuration`, each
with and without the MCP route suffix — **redirect** to the configured authority instead. LakeHold
still publishes no document of its own: a copy would be free to drift from the issuer's, and a client
following the redirect reads the authorization server's own bytes. The request's flavour is
preserved, so an OAuth-only authority is asked for `oauth-authorization-server` and an OIDC one for
`openid-configuration`, rather than LakeHold guessing which the authority serves.

The same class of defect exists at the edge, not only in the API: any host that answers unknown paths
with an SPA shell tells a client that `/authorize`, `/token`, and `/register` exist. Both nginx
configurations return 404 for those and match the whole `/.well-known/oauth-protected-resource`
prefix, so the path-suffixed form a client asks for first is served rather than falling through.

The Codex desktop app, CLI, and IDE extension share this MCP configuration. In the Codex terminal UI,
`/mcp` shows the connection and its tools. If the provider requires explicit scopes, run
`codex mcp login lakehold --scopes <scope-1>,<scope-2>`; otherwise LakeHold's protected-resource
metadata supplies the configured MCP scopes. See the
[official Codex MCP documentation](https://learn.chatgpt.com/docs/extend/mcp) for the shared
configuration and client commands.

#### API-token alternative

For unattended use, open **Users → API tokens**, choose the workspace, narrow the credential to the
catalog the agent needs, retain the reader default, and generate it. Existing credentials and their
last-use state are listed below; revoking one closes its MCP and HTTP access. The plaintext is shown
once. The equivalent public API call is:

```bash
curl -X POST https://lakehold.example.com/api/tenants/demo/tokens \
  -H "Authorization: Bearer $LAKEHOLD_ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name":"claude-agent","role":"reader","catalogName":"analytics"}'
```

Keep the token out of files that get committed. Every example below reads it from the environment.
Launch the client from the shell where `LAKEHOLD_TOKEN` is set.

For Codex:

```bash
export LAKEHOLD_TOKEN='lkh_...'
codex mcp add lakehold-token \
  --url http://localhost:5399/mcp \
  --bearer-token-env-var LAKEHOLD_TOKEN
```

For Claude Code, use environment expansion in its JSON configuration so the token itself is not
written to the project file:

```bash
export LAKEHOLD_TOKEN='lkh_...'
claude mcp add-json lakehold-token \
  '{"type":"http","url":"http://localhost:5399/mcp","headers":{"Authorization":"Bearer ${LAKEHOLD_TOKEN}"}}'
```

The equivalent project-scoped `.mcp.json` entry is below. `type` is **required** alongside `url`,
because an entry with a `url` and no `type` is read as a stdio server and skipped:

```json
{
  "mcpServers": {
    "lakehold": {
      "type": "http",
      "url": "http://localhost:5399/mcp",
      "headers": { "Authorization": "Bearer ${LAKEHOLD_TOKEN}" }
    }
  }
}
```

Codex stores the equivalent configuration in `~/.codex/config.toml` or a trusted project's
`.codex/config.toml`:

```toml
[mcp_servers.lakehold]
url = "http://localhost:5399/mcp"
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
it more often because discovery and inspection tools deliberately attach read-only. Write to a catalog
once (any statement that creates a table will do) before pointing an agent at it. Worth revisiting if
provisioning is ever made to initialise the metadata file at creation time, which would remove the
sharp edge entirely.

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

Maintenance is no longer in this table. `plan_maintenance` is a non-mutating review step and
`apply_maintenance` rechecks the plan's snapshot before changing anything. Both require tenant-owner
capability and the separate **Allow operator commands** switch; apply additionally requires **Allow
write commands**. Backup and restore remain withheld because their long-running Tasks contract is
still unresolved.

| Not exposed | Why |
|---|---|
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

Every statement is recorded with exactly one actor: `TokenId` for an API token or `MemberId` for an
OIDC person, never both. `ActorKind` preserves that classification and `Origin` distinguishes
Workbench, REST, PgWire, MCP, import, and connector execution. Both fields and the nullable member id
land through ordinary PostgreSQL migrations; removing a token or member cannot remove its audit row.

## Phases

Each leaves the product working and is independently testable.

**Phase 1 — the seam. Landed.** `Enforce` lifted into `CapabilityPolicy`, HTTP behaviour proven
unchanged by the existing filter tests, new direct coverage of the rules. No MCP dependency. Still
outstanding from this phase: **verify the 2026-07-28 specification text** against the assumptions
above, which needs the published spec rather than the reporting summarising it.

**Phase 2 — the server. Landed.** `ModelContextProtocol.AspNetCore` **2.0.0** (taken 3 August 2026;
landed originally on `2.0.0-rc.1` under the deliberate decision that nothing here ships as stable
before the SDK does) — see the note in `Directory.Packages.props`. The endpoint is hosted at `Lakehold:Mcp:Route`, guarded by
`McpAuthenticationFilter`, and began with exactly one tool: `query`, read-only. Protected-resource
metadata now ships at both RFC 9728 locations, with the exact MCP resource audience, supported
scopes, and the optional pre-registered public-client extension.

**Phase 3 — discovery. Landed.** `list_tenants`, `describe_schema`, and the schema resource. The
separately specified `list_catalogs` was dropped for the reason given in the tool table. An agent can
now start from `list_tenants` and work down to a query without being told anything in its prompt,
which is what turns the surface from a proven seam into something usable.

**Phase 4 — the differentiated tools. Landed.** `list_snapshots` and `list_changes` — time travel and
CDC, the two capabilities `COMPETITIVE-RESEARCH.md` says are genuinely ahead of the closest peer. Both
emit the same change vocabulary the REST feed and the webhooks use, so an agent and a webhook consumer
do not read two names for one event.

**Phase 5 — landed, with long-running operations still intentionally withheld.**

- **Writes behind an explicit runtime setting — landed.** Allow writes plus a read-write
  credential, as a separate `execute` tool for the annotation reason above.
- **RFC 9728 protected-resource metadata — landed.** See the authentication section.
- **Actor and origin attribution — landed.** Token and member actors are mutually exclusive, and MCP
  is a first-class origin in both the API response and Workbench history.
- **Tasks-based long-running operations — nothing to carry.** SDK 2.0's Tasks extension is the right
  mechanism for a long-running tool call, and this surface deliberately exposes no long-running
  operation. The short maintenance commands use the snapshot-bound plan/apply contract; eject,
  backup, and restore remain withheld. When a long-running operation is exposed, Tasks is how, and
  it must be reconciled with `PUBLIC-API.md`'s `202`/`operationId` model rather than invented twice.
- **The in-product assistant — a separate product.** An assistant in the Angular workbench is an agent
  that would consume this server like any other client. That is where Agent Framework returns, and it
  is a UI decision rather than a continuation of this document.

## Configuration and live settings

The file values below are bootstrap defaults only. They are used until an instance operator saves
System Settings. After that, the PostgreSQL singleton is authoritative for Enabled, PublicBaseUrl,
AllowWrites, AllowOperatorCommands, and MaxRowsPerResult. `Route` remains a startup setting because changing the mapped URL
requires rebuilding the endpoint table.

```jsonc
// appsettings — all non-secret, so it lives in source control (the token does not)
"Lakehold": {
  "Mcp": {
    "Enabled": false,        // production bootstrap; Development overrides this to true
    "Route": "/mcp",
    "PublicBaseUrl": "",     // required behind a reverse proxy; see below
    "AllowWrites": false,    // bootstrap only; System Settings controls the live tool list
    "AllowOperatorCommands": false, // maintenance tier; also requires writes to apply
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

**`McpWriteToolTests`** — the write and operator gates, read from shared runtime settings on every
discovery and call so they can vary without restarting the server.
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
- ~~Query-history attribution asserted end to end.~~ Covered for both API-token and OIDC-member MCP
  calls, including mutual exclusion and `Origin = Mcp`.
- Verified behaviour 7 asserted directly: a range whose *explicit* end predates the table's creation.
  The tools default the end to the newest snapshot, so the trap is only reachable by passing
  `toSnapshot` deliberately, and that path is forwarded but not yet covered.

## Documentation obligations

Shipping this is not done until:

- ~~This document records what landed.~~ Done, and it records the gaps too.
- ~~`AGENT.md` carries the invariant and the repository-map entry.~~ Done (invariant 21).
- ~~`ARCHITECTURE.md`'s matrix moves the AI / MCP row to ✅ and the roadmap moves it out of Next.~~
  Done; it now names the read and operator-gated surfaces.
- ~~`web/lakehold-ui/src/app/docs.content.md` gains a section.~~ Done; it includes local OAuth setup
  for Codex and Claude Code and links back here for the complete tool and token reference.
- ~~`README.md` shows the connection snippet an agent client needs.~~ Done; the quick start carries
  the local commands and this document remains the full reference.

## Open questions

- ~~.NET 10 target support in SDK 2.0.0.~~ Confirmed: the package ships a `net10.0` target, and the
  API builds and runs against it.
- ~~When to move off `2.0.0-rc.1`.~~ Settled 3 August 2026: 2.0.0 stable published and the
  dependency moved to it.
- ~~Whether `RouteCapability` is renamed.~~ Settled: moved in Phase 1, renamed to `Capability`
  immediately after, for the reason given above.
- ~~Whether the MCP endpoint is separately toggleable.~~ Settled: a shared runtime setting,
  bootstrapped by `Lakehold:Mcp:Enabled`. Production starts false; development starts true.
