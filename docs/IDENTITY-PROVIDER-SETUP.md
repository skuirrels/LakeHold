# Signing in, and swapping the identity provider

How a human gets into the Workbench, and how to replace the built-in credential flow with an
external identity provider such as Keycloak.

[`AUTHENTICATION.md`](AUTHENTICATION.md) explains *why* the model is shaped this way. This document
is the operational one: run these steps and you are signed in.

## The two sign-in mechanisms, and when each applies

LakeHold has two, and knowing which one you are using explains most of what you see:

| | API token | OIDC browser session |
|---|---|---|
| Looks like | `lkh_…` pasted into the Workbench | "Continue with your identity provider" |
| Sent as | `Authorization: Bearer lkh_…` | `LakeHold.Session` cookie |
| Lives in | Browser tab memory (`sessionStorage`) | Server-signed cookie, 8-hour sliding |
| Survives a tab close | **No** — paste it again | **Yes** |
| Who it is for | Scripts, BI tools, agents, the PostgreSQL wire endpoint | Humans |
| Needs an IdP | No | Yes |

Both resolve to the same principal internally, so a route behaves identically whichever you used.
**Without an identity provider configured, only the token mechanism exists** — that is the default,
and it is why a fresh install asks for a token rather than showing a login form.

A note on the tab-close behaviour, because it surprises people: it is deliberate. A pasted machine
credential is long-lived and unrecoverable, so it is kept out of durable browser storage. The
durable answer for a human is an OIDC session, which is what the second half of this document sets
up. If a human session exists, the Workbench clears any pasted token so the two cannot disagree.

## Part 1 — First sign-in with no identity provider

This is the path to a working login in about two minutes, and the one to use for verification.

### 1. Start the stack

```bash
cp .env.example .env && docker compose up -d
```

The Workbench is at <http://localhost:5399> and the API at <http://localhost:5200>. (On
`compose.production.yaml` there is no separate UI port — nginx serves the site and proxies `/api`
on <http://localhost:8080>.)

To run the two app processes on the host instead, start only the backing services and then:

```bash
dotnet run --project src/Lakehold.Api
```

```bash
npm start --prefix web/lakehold-ui
```

### 2. Take the bootstrap token from the API log

On a node with no tokens at all, LakeHold mints one instance-scoped token and logs it **once**:

```
No API tokens existed, so a bootstrap instance token was minted. It is shown ONCE and cannot be
recovered — store it now: lkh_admin_…
```

```bash
docker compose logs api | grep "bootstrap instance token"
```

This is the single deliberate exception to never logging a credential: it authenticates
*provisioning only* — it creates tenants, catalogs, and tokens, and deliberately cannot read data.
Copy it now; it is not recoverable.

To set it yourself instead of having one minted, put `LAKEHOLD_BOOTSTRAP_TOKEN=lkh_admin_<random>`
in `.env` before first start.

### 3. Sign in and create the first workspace

1. Open <http://localhost:5399/workbench>. The panel says **"Sign in to this LakeHold node"**.
2. Paste the `lkh_admin_…` bootstrap token and press **Sign in**.
3. Because the node is empty you get **"No workspaces yet"**. Fill in a workspace slug (for example
   `acme`), an optional display name, and a catalog name (for example `analytics`), then create it.
4. LakeHold provisions the tenant and catalog and mints an **owner** token for it, shown once.
   Save it — this is the credential for scripts, BI tools, and the PostgreSQL wire endpoint.
5. Press **"I have saved it — open the workspace"**. You are in the SQL IDE.

### 4. Create further credentials for other people

With the bootstrap (instance) token you can reach **Settings → token administration**, which lists,
mints, and revokes tenant credentials. Each one takes:

| Field | Meaning |
|---|---|
| Workspace | The tenant the credential is scoped to |
| Catalog | Optional narrowing — blank means every catalog in the workspace |
| Name | How it appears in the listing and audit trail; 1–200 characters |
| Role | `owner` (everything, including maintenance and eject), `editor` (query and write), `reader` (query only) |
| Read-only | Attaches the catalog read-only, so writes fail in the engine rather than in a policy check |
| Expires | Optional; must be in the future |

The secret is displayed once and stored only as a SHA-256 hash, so reading the database yields
nothing usable. Revoking closes the HTTP and PostgreSQL-wire surfaces together.

> **On `Role` and `Read-only` together.** They are not redundant. `reader` is a *role* and
> read-only is an *attachment*: an owner credential with read-only set can still administer tokens
> while being unable to write data. If you only want "can look, cannot touch", pick `reader`.

### 5. Turn enforcement on

The application default for `Lakehold:Auth:RequireAuthentication` is **false**, so a bare
`dotnet run` still accepts requests without a credential and trusts the route.
`compose.production.yaml` sets it to `true`. Set it explicitly for any deployment you care about:

```bash
Lakehold__Auth__RequireAuthentication=true
```

## Part 2 — Swapping in Keycloak

Replaces the pasted token for humans; tokens keep working for machines. LakeHold is a *relying
party*, so it never stores passwords or manages user accounts — Keycloak owns all of that.

### What LakeHold needs from any provider

Three things, whichever provider you use:

1. **An authorization-code flow with PKCE** at a discoverable OIDC authority.
2. **A redirect URI** of `https://<your-lakehold-host>/auth/callback`.
3. **Claims that name the tenant and the role**, because LakeHold maps an identity onto a tenant by
   reading a claim — it has no user table of its own to join against.

### 1. Create the realm and client in Keycloak

Realm: `lakehold` (any name; it becomes part of the authority URL).

Create a client:

| Setting | Value |
|---|---|
| Client ID | `lakehold-workbench` |
| Client authentication | **On** (confidential client — LakeHold holds the secret server-side) |
| Standard flow | Enabled |
| Direct access grants | Disabled |
| Valid redirect URIs | `https://lakehold.example.com/auth/callback` |
| Valid post logout redirect URIs | `https://lakehold.example.com/workbench` |
| Web origins | `https://lakehold.example.com` |

Copy the generated secret from **Credentials**. PKCE is always on in LakeHold's handler, so you may
additionally set the client's *Proof Key for Code Exchange Code Challenge Method* to `S256`.

For local evaluation over plain HTTP, set `LAKEHOLD_OIDC_REQUIRE_HTTPS_METADATA=false` and use
`http://localhost:5399/auth/callback`. Never do this off a development machine.

### 2. Add the claim mappers

This is the step people miss, and its symptom is a successful login that lands back on the sign-in
panel. LakeHold reads three claims:

| Claim | Default key | Purpose | If absent |
|---|---|---|---|
| Tenant | `tenant` | Which workspace this human belongs to | **Sign-in resolves to nothing** — an identity that names no tenant cannot be served |
| Role | `role` | `owner`, `editor`, or `reader` within that tenant | Defaults to **editor** — query and write, but not maintenance or eject |
| System admin | `lakehold_admin` | Instance administrator: provisions tenants and tokens, cannot read tenant data | Not an administrator |

In Keycloak, on the client's **Client scopes → `lakehold-workbench-dedicated` → Add mapper**:

- **User Attribute** mapper named `tenant`, user attribute `tenant`, token claim name `tenant`,
  added to the ID token and access token, claim type String.
- **User Attribute** mapper named `role`, user attribute `lakehold_role`, token claim name `role`,
  same token placement.
- For the administrator claim, either a **User Attribute** mapper emitting `lakehold_admin` = `true`,
  or a **Group Membership** mapper with token claim name `groups` — then set
  `LAKEHOLD_OIDC_SYSTEM_ADMIN_CLAIM=groups` and
  `LAKEHOLD_OIDC_SYSTEM_ADMIN_VALUE=lakehold-administrators` so membership of that group is what
  grants it.

Then set the attributes on each user (**Users → *user* → Attributes**): `tenant=acme`,
`lakehold_role=owner`.

> **Any claim you map is a grant.** The system-admin claim confers instance-wide provisioning. Emit
> it from a group or role you control centrally, never from a user attribute an end user can edit
> in a self-service profile.

### 3. Configure LakeHold

In `.env` (secrets only — the rest can live in compose or `appsettings`):

```bash
LAKEHOLD_OIDC_AUTHORITY=https://keycloak.example.com/realms/lakehold
LAKEHOLD_OIDC_AUDIENCE=lakehold-workbench
LAKEHOLD_OIDC_CLIENT_ID=lakehold-workbench
LAKEHOLD_OIDC_CLIENT_SECRET=<from Keycloak Credentials>
LAKEHOLD_OIDC_REQUIRE_HTTPS_METADATA=true
LAKEHOLD_OIDC_TENANT_CLAIM=tenant
LAKEHOLD_OIDC_ROLE_CLAIM=role
LAKEHOLD_OIDC_SYSTEM_ADMIN_CLAIM=groups
LAKEHOLD_OIDC_SYSTEM_ADMIN_VALUE=lakehold-administrators
```

Both `compose.yaml` and `compose.production.yaml` already map these onto `Lakehold__Oidc__*`.

**Set `LAKEHOLD_OIDC_AUDIENCE`.** Leaving it empty disables audience validation entirely, which
means *any* token that realm issued is accepted — including one minted for a different application
sharing the realm. LakeHold logs a warning at start-up when this is the case; treat it as an error
in production.

`Scopes` is not mapped in compose because the defaults suffice. To request extra scopes, add them
as indexed environment variables:

```bash
Lakehold__Oidc__Scopes__0=groups
```

### 4. Verify

Restart, then:

```bash
curl -s http://localhost:5200/auth/session
```

It returns `{ "oidcEnabled": …, "authenticated": …, "displayName": …, "systemAdmin": … }`.
`oidcEnabled: true` means the client id was picked up. Open `/workbench`; the panel now offers
**"Continue with your identity provider"** above the token field. Sign in, and `/auth/session`
should report `authenticated: true` with your display name.

The relevant endpoints:

| Route | Purpose |
|---|---|
| `GET /auth/login?returnUrl=/workbench` | Starts the authorization-code flow |
| `GET /auth/session` | Reports whether browser login is configured and whether you are signed in |
| `GET /auth/logout` | Clears the LakeHold session cookie |
| `GET /auth/callback` | The provider's redirect target — register this URI |

### Troubleshooting

| Symptom | Cause |
|---|---|
| Login succeeds, back at the sign-in panel | No `tenant` claim, or its value does not match an existing tenant slug. Check the mapper and that the tenant exists. |
| "Continue with your identity provider" never appears | `ClientId` is empty. Browser login requires both an authority *and* a client id. |
| `/auth/login` returns 404 | Same cause — the endpoint refuses when browser login is not configured. |
| Redirect loop or `invalid_redirect_uri` | The redirect URI in Keycloak must be exactly `<origin>/auth/callback`. |
| Works on localhost, fails deployed | `RequireHttpsMetadata` is true and the authority is HTTP, or the cookie needs HTTPS. Terminate TLS in front of the API. |
| Everyone is an editor | No `role` claim is being emitted; editor is the documented default. |
| Signed in but cannot run maintenance or eject | Those are owner-only. Emit `role=owner`. |

## What this does not change

- **Machine credentials are unaffected.** `lkh_…` tokens keep working alongside OIDC, and the
  PostgreSQL wire endpoint continues to use them. Revocation still closes both surfaces at once.
- **LakeHold still has no user table.** Identity, password policy, MFA, and lifecycle belong to the
  provider. What LakeHold stores is the mapping from a claim to a tenant and a role.
- **Air-gapped installs are unaffected.** Leaving `Authority` empty leaves the whole path off, so
  no dependency on an unreachable provider is ever acquired.
