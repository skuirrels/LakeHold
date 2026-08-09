# Identity: signing in, adding users, and connecting clients

How a person gets into the Workbench, how you decide what they reach, how to run a production node
for the first time, and how to swap the bundled identity provider for your own.

[`AUTHENTICATION.md`](AUTHENTICATION.md) explains *why* the model is shaped this way. This document
is the operational one: follow it and you are signed in, with users and clients working.

## The model in one paragraph

LakeHold **federates authentication and owns authorization**. Your identity provider proves who
someone is; LakeHold decides what that identity reaches, and it decides it from a membership record
you can see, change, and revoke in the product. There is no user table and no password anywhere in
LakeHold — deliberately, because your provider already owns lifecycle, MFA, and offboarding, and a
second account store is a second place to forget to disable someone.

Three kinds of caller, three mechanisms:

| Caller | Authenticates with | Authorized by |
|---|---|---|
| **Users** | Browser sign-in at your identity provider | A membership row you manage under **Users** |
| **Clients** — BI tools, scripts, SDKs, the PostgreSQL wire endpoint | An API token (`lkh_…`) | The role and catalog scope baked into that token |
| **Agents** — MCP clients such as Claude or Codex | An API token, or OAuth authorization code with PKCE as a person | The token role or the person's membership, plus operator switches for writes and operator commands |

## Part 1 — Running a production node for the first time

### 1. Start it

```bash
cp .env.example .env
docker compose -f compose.production.yaml up -d
```

Authentication is required; there is no mode in which it is not. The site is on
<http://localhost:8080>, with nginx serving it and proxying `/api` on the same origin.

### 2. Take the bootstrap token from the log

A node with no credentials mints one instance token and logs it **once**:

```bash
docker compose -f compose.production.yaml logs api | grep "bootstrap instance token"
```

This is the single deliberate exception to never logging a credential: it authenticates
*provisioning only* — it creates tenants, catalogs, and credentials, and deliberately **cannot read
tenant data**. Copy it now; it is not recoverable. To set it yourself instead, put
`LAKEHOLD_BOOTSTRAP_TOKEN=lkh_admin_<random>` in `.env` before the first start.

### 3. Create the first workspace

Open the site, paste the bootstrap token, and create a workspace (a tenant) and a catalog. LakeHold
mints an **owner** token for it, shown once. That token is how scripts, BI tools, and the wire
endpoint reach the workspace.

At this point you have a working node administered by tokens. Everything below is about users.

### 4. Point it at your identity provider

Set these and restart. [Part 3](#part-3--using-your-own-identity-provider) covers what they mean for
a provider other than Keycloak.

```bash
LAKEHOLD_OIDC_AUTHORITY=https://idp.example.com/realms/lakehold
LAKEHOLD_OIDC_AUDIENCE=lakehold-workbench
LAKEHOLD_OIDC_CLIENT_ID=lakehold-workbench
LAKEHOLD_OIDC_CLIENT_SECRET=…            # from your provider
LAKEHOLD_OIDC_SYSTEM_ADMIN_CLAIM=groups
LAKEHOLD_OIDC_SYSTEM_ADMIN_VALUE=lakehold-administrators
```

A **Sign in** button now appears. Sign in as someone in your administrator group and you land on
instance administration.

## Part 2 — Adding users

### They sign in first, then you admit them

There is no invite email. A person signs in with your provider, LakeHold records them, and you decide
what they reach. That ordering is deliberate: you are approving an identity your provider has already
authenticated, rather than creating an account that then needs one.

1. Send them the site URL. They click **Sign in** and authenticate.
2. They appear under **Users**, and until you act they reach nothing.
3. Choose a role and click **Admit**.

If your provider emits a `tenant` claim naming an existing workspace, a first-time arrival is
admitted automatically with the role from the `role` claim. That claim is honoured **once**, to open
the door. After that the membership row is authoritative — so demoting someone in LakeHold is not
quietly undone the next time their provider re-asserts a stale role.

### The roles

| Role | Can |
|---|---|
| `owner` | Everything in the workspace: query, write, maintenance, eject — and administer its users and credentials |
| `editor` | Query and write. Not destructive maintenance, not eject |
| `reader` | Read only, enforced by attaching the catalog read-only rather than by a policy check |

### Suspend, remove, and why both exist

**Suspend** keeps the person listed and refuses them, so past activity still has a name against it,
and restoring is one click. **Remove** discards the record; they return as a new arrival if they sign
in again. Either takes effect on their next request.

### Who can administer users

An instance administrator, for any workspace; and a workspace **owner**, for their own. Both see
**Administration** in the navigation.

## Part 3 — Using your own identity provider

The bundled Keycloak in `compose.yaml` is a worked example for development, not a dependency. Any
OIDC provider works — Entra, Okta, Auth0, Authentik, Keycloak — if it can do three things.

### 1. An authorization-code flow with PKCE

Register LakeHold as a **confidential** client. LakeHold exchanges the code server-side and the
browser never receives an identity-provider token; what it holds afterwards is LakeHold's own
8-hour sliding session cookie.

- Redirect URI: `https://<your-host>/auth/callback`
- Post-logout redirect URI: `https://<your-host>/auth/signed-out`

  This one is required, not decorative. **Sign out** is an RP-initiated logout — LakeHold ends the
  provider's session as well as its own, or the next sign-in would silently return the same person
  from a surviving provider session. The provider validates that redirect against the client and
  refuses the sign-out if it is not registered. LakeHold sends `client_id` rather than an
  `id_token_hint`, because it deliberately never stores the id token.

### 2. An audience

Set `LAKEHOLD_OIDC_AUDIENCE`, and make the provider put that value in the access token's audience.

This value is required whenever an authority is configured. LakeHold refuses to start without it;
accepting every token minted by the issuer is not a supported mode. Ordinary API and Workbench bearer
tokens must carry this audience. MCP bearer tokens must instead carry the exact MCP resource URL
advertised by protected-resource metadata, for example `https://lakehold.example.com/mcp`.

### 3. Two or three claims

| Claim | Setting | Purpose |
|---|---|---|
| `tenant` | `LAKEHOLD_OIDC_TENANT_CLAIM` | Optional. Names the workspace a first-time arrival joins automatically. Without it, arrivals are admitted by hand. |
| `role` | `LAKEHOLD_OIDC_ROLE_CLAIM` | Optional. The role automatic admission uses. Defaults to `reader`. |
| system admin | `LAKEHOLD_OIDC_SYSTEM_ADMIN_CLAIM` / `_VALUE` | **Required for administrators.** Instance administration stays a provider assertion, so a workspace owner cannot promote themselves. |

Only the third is genuinely required, and only for administrators. Everything else can be done by
admitting users in the Workbench — which is the point of the membership model: you are not obliged
to encode LakeHold's authorization model in your directory.

**Emit the administrator claim from a group or directory role you control centrally — never from a
user attribute someone can edit in a self-service profile.** Any claim you map is a grant.

### When the server and the browser reach the provider differently

Behind a proxy, or in containers, the browser and the API often use different names for the same
provider. The issuer is baked into every token and must stay the name the browser used, so set the
authority to the public one and fetch discovery from the reachable one:

```bash
LAKEHOLD_OIDC_AUTHORITY=https://idp.example.com/realms/lakehold
LAKEHOLD_OIDC_METADATA_ADDRESS=http://idp-internal:8080/realms/lakehold/.well-known/openid-configuration
```

### Turning the bundled provider off

Clear `LAKEHOLD_OIDC_AUTHORITY` and the whole OIDC path stays off — no dependency on a provider an
air-gapped install cannot reach. Sign-in is then by API token only. To stop the container too:

```bash
docker compose stop keycloak
```

The development realm, its two seeded users, and how to give either the other capability are in
[`deploy/keycloak/README.md`](../deploy/keycloak/README.md).

## Part 4 — Clients and agents

Both can use API tokens. Issue them under **Users → API tokens**, or over HTTP:

```bash
curl -X POST https://<your-host>/api/tenants/<workspace>/tokens \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"powerbi","role":"reader","catalogName":"analytics","expiresUtc":null}'
```

The secret is returned once and stored only as a SHA-256 hash. Revoking closes the HTTP, MCP, and
PostgreSQL-wire surfaces together.

### Choosing scope

| Field | Guidance |
|---|---|
| **Name** | Appears in the audit trail. Name it for the thing that holds it, not the person who made it |
| **Role** | `reader` unless it must write. This is the field that matters most |
| **Catalog** | Narrow to one catalog for the tightest credential; blank grants every catalog in the workspace |
| **Read-only** | Attaches the catalog read-only. Useful with `editor`/`owner` when a client must never write |
| **Expiry** | Optional, and must be in the future |

### Agents specifically

MCP is at `/mcp` and **always requires a credential**, even where other surfaces might not. Writes
have two independent gates: the instance-level *Allow write commands* switch, and the caller's own
role. A read-only agent token produces a read-only *attachment*, so a write fails in the engine
rather than in a check the agent might talk its way around.

Give each machine agent its own token with a short expiry, so revoking one does not disturb the
others. To let an interactive agent sign in **as the person operating it**, register a second public,
PKCE-only OIDC client and set:

```bash
LAKEHOLD_OIDC_MCP_CLIENT_ID=lakehold-mcp
```

The registration has no client secret. Allow the loopback callback URIs your MCP client documents,
emit the same membership claims as the Workbench client, and configure the access-token audience as
the public MCP endpoint. Behind a proxy, first save the externally reachable **Public base URL** in
System Settings; it is required for the resource identifier and callback discovery to be truthful.
LakeHold advertises `scopes_supported`, the resource URL, and an optional `client_id` extension from
its RFC 9728 document. Configure clients that support a pre-registered OAuth id explicitly; for
example, Codex accepts the client id and discovers the RFC 8707 resource from that document:

```bash
codex mcp add lakehold \
  --url https://lakehold.example.com/mcp \
  --oauth-client-id lakehold-mcp
codex mcp login lakehold
```

When scopes are required, pass them to `codex mcp login` with `--scopes`. Do not also configure
`--oauth-resource`: Codex reads the exact value from LakeHold's protected-resource metadata.
Supplying the same value explicitly duplicates the `resource` parameter, which providers such as
Keycloak reject. Omitting the client id is also incorrect for a provider without dynamic client
registration; Codex must be told to use the public client the operator registered.

Claude Code also accepts a pre-registered public client. It discovers the resource and authorization
server from LakeHold's protected-resource metadata, so it does not need a separate resource flag:

```bash
claude mcp add --transport http --client-id lakehold-mcp \
  lakehold https://lakehold.example.com/mcp
claude mcp login lakehold
```

If `claude mcp login` is not available in the installed version, open Claude Code, run `/mcp`, select
`lakehold`, and authenticate there. Do not pass `--client-secret`: this registration is public and
PKCE-only.

For the bundled development stack, the exact commands are:

```bash
codex mcp add lakehold \
  --url http://localhost:5399/mcp \
  --oauth-client-id lakehold-mcp
codex mcp login lakehold

claude mcp add --transport http --client-id lakehold-mcp \
  lakehold http://localhost:5399/mcp
claude mcp login lakehold
```

Sign in as `analyst` with password `lakehold` to reach the seeded `demo` workspace. The complete
smoke-test prompt, API-token alternative, and troubleshooting steps are in [`MCP.md`](MCP.md#connecting-a-client).

Provider setup:

- **Keycloak:** create an OpenID Connect client, enable Standard flow, select public client
  authentication, require PKCE `S256`, add the client's loopback redirect patterns, and add an
  audience mapper for the complete MCP endpoint URL. Copy the Workbench tenant, role, and groups
  protocol mappers. The bundled development realm includes `lakehold-mcp` as a worked example.
- **Microsoft Entra:** create a separate app registration, add the MCP client's loopback URI under
  the mobile/desktop public-client platform, enable public-client flows, and expose or map the MCP
  resource audience plus the claims LakeHold uses. Use its Application (client) ID as
  `McpClientId`; do not create a secret for the native/public client.
- **Okta:** create a Native Application using Authorization Code and Refresh Token, require PKCE,
  register the client's loopback sign-in redirects, and add an authorization-server audience and
  claims matching the MCP endpoint and LakeHold membership contract.

The `client_id` metadata member is an extension, so clients may ignore it; configure those clients
with the same id explicitly. Dynamic client registration is not required and may remain disabled at
the provider.

### Connecting a BI tool

Power BI, Tableau, DBeaver, and `psql` connect through the PostgreSQL wire endpoint using these same
tokens. See [`POSTGRES-WIRE.md`](POSTGRES-WIRE.md); Power BI still needs the documented
type-catalogue shim.

## Troubleshooting

| Symptom | Cause |
|---|---|
| Sign-in succeeds, then there is nothing to open | Authenticated but not yet admitted. An administrator admits you under **Users**. |
| A person is not in the Users list | They have never signed in. The list is users LakeHold has seen, not everyone in your directory. |
| "Continue with your identity provider" never appears | `CLIENT_ID` is empty. Browser login needs both an authority *and* a client id. |
| `/auth/login` returns 404 | Same cause. |
| `invalid_redirect_uri` at the provider | The registered URI must be exactly `<origin>/auth/callback`. |
| Everyone lands as a reader | No `role` claim is emitted, which is fine — set roles under **Users** instead. |
| Nobody can administer anything | The system-admin claim is not configured or not emitted. It is the one claim you cannot work around in the UI. |
| Works locally, fails deployed | `RequireHttpsMetadata` with an HTTP authority, or the session cookie needs HTTPS. Terminate TLS in front of the API. |
| Signing out and back in returns the same person | Fixed in 2.2.3: `/auth/logout` now signs out of the OIDC scheme too, so the provider ends its own session. On an older release, end the provider session separately or use a private window. If it still recurs, the provider is refusing the sign-out — check that your client allows `<public-url>/auth/signed-out` as a post-logout redirect URI. |
| MCP login attempts dynamic registration and the provider returns `403` | The client was added without the configured public client id. Re-add it with `--oauth-client-id <McpClientId>`; dynamic registration is not required. |
| MCP login returns `invalid_request: duplicated parameter` | The client configured the resource explicitly even though LakeHold already advertises it. Remove `--oauth-resource` and re-add the server with only its URL and public client id. |
| Development MCP login says `Client not found` after an upgrade | The existing Keycloak container skipped the changed realm import. Run `docker compose up -d --force-recreate --wait keycloak`; this recreates development identity state without removing LakeHold data volumes. |
| MCP login opens `https://<lakehold>/authorize` and 404s | The client looked for authorization-server metadata on LakeHold's origin, found none, and fell back to assuming the MCP server is its own authorization server. Fixed in 2.2.1, where those discovery paths redirect to the configured authority. Before that release there is no client-side workaround: upgrade, or use an API token instead of a browser sign-in. |

## Creating a user from LakeHold

There are two ways LakeHold gets used, and only one of them makes "add them in your provider" a
reasonable answer.

**Federated.** A corporate directory already holds everybody. LakeHold should not create users
there — it does not own that directory, and the people exist before LakeHold does. This is the
default and needs nothing below.

**Provider-as-implementation-detail.** Keycloak is deployed *for* LakeHold and holds nobody else.
Here "add them in your provider" means an operator learning another product's admin console to
onboard a colleague, for an identity that exists only to reach this one. That is the case
**user provisioning** exists for.

It is **off unless configured**, because turning it on has a real cost, stated plainly:

> To create a user in your provider, LakeHold must hold a credential that can create users in your
> provider. Today it holds none, and that is a property worth naming before giving it up: whoever
> compromises LakeHold cannot currently mint an identity. With provisioning configured, they can —
> bounded by whatever that credential is scoped to.

So the credential is scoped as narrowly as the provider allows, and LakeHold asks for exactly one
capability: **manage users, in one realm**. Never realm administration, never client or role
management. On Keycloak that is a service-account client granted the `manage-users` realm-management
role and nothing else. The bundled development realm registers `lakehold-provisioner` that way.

### What it does, and what it deliberately does not

Creating a user does two things in one operation: the identity in the provider, and the
`TenantMember` row that decides what it reaches. The membership is the part LakeHold owns; the
identity is the part it is borrowing the provider's authority for.

A new user is created with a **temporary password, shown to the administrator once** and never
stored, alongside a provider-side required action to change it at first sign-in. This mirrors how
API tokens already work here, and it avoids making SMTP a prerequisite for adding a colleague. If
your provider sends email, prefer that: set `Lakehold:Oidc:Provisioning:UseProviderEmail` and
LakeHold asks the provider to send its own invitation instead of returning a password at all.

LakeHold does not manage passwords after creation, does not reset them, does not configure MFA, and
does not synchronise a directory. Those remain the provider's, and the sections above still apply:
what somebody *reaches* is decided by the membership row, not by anything set at creation time.

## What this does not do

- **No password management, MFA, or SCIM.** Those belong to your provider. LakeHold can *create* a
  user where provisioning is configured (above); it does not become a directory.
- **Instance administration is not manageable in-product.** It comes from a provider claim by
  design, so a workspace owner cannot promote themselves.
- **Removing someone from your directory does not delete their LakeHold membership.** It stops them
  signing in, which is what matters; remove the row too if you want them off the list.
