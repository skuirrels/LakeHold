# The PostgreSQL wire endpoint

Lakehold speaks enough of the PostgreSQL frontend/backend protocol for Power BI, Tableau, Metabase,
DBeaver, `psql`, and Npgsql to connect to a tenant's catalog as though it were a Postgres database.

This document is the specification and the record of what is and is not implemented. It is the
companion to [`ARCHITECTURE.md`](ARCHITECTURE.md), which explains why this is *parity* rather than a
differentiator: every competitor already has it, and its absence was the single thing keeping the BI
tools out.

## Why a wire protocol rather than a connector

A custom Power BI connector is a `.mez` file to sign, distribute, install, and maintain per BI tool.
The wire protocol is written once and every tool that already speaks Postgres — which is all of
them — connects with no artifact at all. MotherDuck shipped a Postgres endpoint in 2026 for the same
reason, and Power BI worked against it out of the box.

## Connection model

| Postgres concept | Lakehold concept |
|---|---|
| `user` | tenant slug |
| `database` | catalog name |
| password | shared secret from configuration |
| session | one `Duckling`, resolved exactly as an HTTP query resolves |

```
Host=localhost  Port=5433  Database=analytics  Username=demo  Password=…
```

This maps onto `LakehouseService.ExecuteAsync(tenantSlug, catalogName, …)` one-for-one, so the wire
endpoint enters the engine through the same seam the HTTP API does. Tenant isolation therefore
remains structural (invariant 4): a connection reaches exactly the catalog attached to the session
its `user`/`database` pair resolved to, and no SQL it submits is inspected to enforce that.

## Authentication

The API has no authentication (see [`ARCHITECTURE.md`](ARCHITECTURE.md)), so this endpoint carries
its own. It implements the protocol's password exchange against **per-tenant credentials**:

```jsonc
// .env — these are secrets
Lakehold__PgWire__TenantPasswords__demo=…
Lakehold__PgWire__TenantPasswords__acme=…
```

A per-tenant password authenticates *that tenant only*. This matters more than it sounds: a single
shared password authenticates the connection but not the tenant it named, so any holder of it could
present themselves as any tenant — on a multi-tenant node, one credential was every credential. The
attachment boundary was doing its job and the credential was undermining it from outside.

When any per-tenant password is configured they are authoritative, and a tenant without one is
refused rather than falling back to the shared secret — a fallback would mean adding the first
tenant's credential silently left every other tenant on the shared one. A tenant with no entry is
refused *identically* to a wrong password, challenge included, so the response does not disclose
which tenant names exist.

`Password` remains for single-tenant deployments, where the distinction is meaningless.

Mechanics:

- `AuthenticationMD5Password` by default — the password is salted and hashed per connection, so it
  does not cross an unencrypted socket in the clear.
- `AuthenticationCleartextPassword` when `Lakehold:PgWire:AllowCleartextPassword` is set, for clients
  that no longer implement MD5.
- The endpoint **refuses to start** when enabled without a password, unless
  `AllowAnonymous` is explicitly set. Failing closed is the point: this is a database port.

What this is not: per-user identity, roles, or an audit trail of *who* connected. Every connection
presenting the shared secret is the tenant it names. That is strictly better than the HTTP API
(which asks for nothing at all) and strictly worse than what real authentication will provide. It is
a stopgap with an expiry date, not the design.

## TLS

Configure a certificate and the endpoint negotiates encryption on `SSLRequest`:

```jsonc
{
  "Lakehold": {
    "PgWire": {
      "TlsCertificatePath": "/etc/lakehold/wire.pfx",   // .pfx/.p12, or a .pem with a key path
      "TlsCertificateKeyPath": "",                       // PEM only; a bundle carries its own key
      "RequireTls": false                                // refuse clients that will not encrypt
    }
  }
}
```

TLS 1.2 is the floor — older versions are broken rather than merely dated, and a database port is
the last place to keep them for compatibility. `TlsCertificatePassword` is a secret and belongs in
`.env`.

Three behaviours worth knowing:

- **Without a certificate the endpoint declines encryption and continues in plaintext**, which is
  what `psql` and Npgsql expect when a server has no TLS. That is the previous behaviour, preserved.
- **`RequireTls` refuses a client that never asks.** Rejecting only in the negotiation path would
  leave a client that skips `SSLRequest` entirely with a plaintext session, so the check also covers
  a startup packet arriving unencrypted. With it set, the process refuses to start without a
  certificate rather than refusing every connection at run time.
- **A certificate that cannot be loaded, or has no private key, is logged as an error** and the
  endpoint falls back to plaintext unless `RequireTls` is set. The distinction is deliberate: a
  deployment that asked for TLS and cannot serve it should fail loudly, not quietly downgrade.

## Protocol surface

Implemented:

| Message | Direction | Notes |
|---|---|---|
| `SSLRequest` | → | Declined with `N` |
| `CancelRequest` | → | Accepted and ignored; the socket is closed |
| `StartupMessage` | → | `user`, `database`; other parameters ignored |
| `AuthenticationMD5Password` / `CleartextPassword` / `Ok` | ← | |
| `PasswordMessage` | → | |
| `ParameterStatus` | ← | `server_version`, `client_encoding`, `DateStyle`, `TimeZone`, … |
| `BackendKeyData`, `ReadyForQuery` | ← | |
| `Query` (simple) | → | Split into statements; each answered with its own result set |
| `Parse`, `Bind`, `Describe`, `Execute`, `Sync`, `Close`, `Flush` | → | Extended query protocol |
| `RowDescription`, `DataRow`, `CommandComplete`, `EmptyQueryResponse` | ← | |
| `ParseComplete`, `BindComplete`, `NoData`, `ParameterDescription` | ← | |
| `ErrorResponse` | ← | Engine errors returned verbatim; `FATAL` when the connection is closing |
| `Terminate` | → | |

Deliberately not implemented:

- **Bound parameters.** A `Bind` carrying parameter values is refused with an error rather than
  guessed at. Textual substitution would be a SQL-injection surface inside the tenant's own session
  and a silent correctness risk; the provider's parameterised path is the correct fix and is the
  first follow-up.
- **Row-limited `Execute`.** A client asking for part of a portal is refused rather than served
  approximately. Honouring it means holding a streaming reader — and therefore a `Duckling` and its
  session gate — open across messages, which is the opposite of the per-statement session model
  below. Returning everything anyway ignores what the client asked for, and re-running the statement
  on the next `Execute` would resend rows it already has: one is a protocol violation, the other is
  silent duplication. `PortalSuspended` is consequently never sent.
- **COPY, function-call, and replication protocols.**
- **Real transactions.** `BEGIN`/`COMMIT`/`ROLLBACK` are acknowledged rather than executed, because a
  connection resolves a fresh session per statement and so has nothing to hold a transaction open
  across. This is safe for the read traffic a BI tool generates, and DuckLake serialises writes
  through the session gate regardless — but a client that needs real transactional writes wants the
  HTTP path, not this one.

### Binary format is required, not optional

The first draft of this endpoint returned every value as text and refused binary result formats. A
real client rejected it, which is the reason the test suite drives Npgsql rather than a hand-written
client.

Npgsql resolves a converter per column from the type OID and the declared format, and for several of
the types that matter most to a BI tool — `int8`, `numeric`, timestamps — it has **no text-format
read path at all**. Declaring those columns as text does not make reading them slower; it makes the
client throw. So the endpoint implements PostgreSQL's binary encodings, including the base-10000
`numeric` layout and the 2000-01-01 epoch used by dates and timestamps.

The format is chosen per statement: the simple query protocol is text by definition, and the extended
protocol follows the format codes the client sent in `Bind`.

### Deferred Describe

The protocol asks for a row description at `Describe`, before `Execute` has run anything — but the
row shape of arbitrary SQL is only knowable by executing it. Answering `NoData` and sending a
`RowDescription` later makes the client reject the rows that follow; planning the statement twice to
learn its shape would execute every query twice.

The endpoint instead defers the `Describe` reply until `Execute` produces the columns, which puts
`RowDescription` exactly where the protocol's own ordering requires it. Clients batch
`Parse`/`Bind`/`Describe`/`Execute`/`Sync` and then read, so the deferral is invisible on the wire.

### Command tags carry the affected-row count

A client learns what a write did from the completion tag: `INSERT 0 12`, `UPDATE 7`, `DELETE 3`.
Npgsql turns that number into the value `ExecuteNonQuery` returns, so it is not decoration.

The endpoint used to send `INSERT 0 0` for every successful write, because the provider's dynamic
path cannot report a count — DuckDB.NET's reader exposes `RecordsAffected == -1`, so a DML statement
streams back with no columns and no rows and is indistinguishable from a statement that returned
nothing. `ExecuteNonQuery` on the same connection *does* report the count, so a statement whose
leading keyword is `INSERT`, `UPDATE`, `DELETE`, or `MERGE` — and which carries no `RETURNING` — takes
the materialising path (`Duckling.ExecuteQueryAsync`), which executes it as a non-query and reports
what it changed.

Two boundaries are worth stating, because the classification looks like SQL parsing and is not:

- **It is a reporting choice, never a security one.** Isolation is still which catalog is attached to
  the session (invariant 4). Nothing here filters, rewrites, or authorises a statement, and a
  statement the classifier does not recognise — a CTE-led write, say — simply streams as before and
  reports no count. The unrecognised case loses a number; it cannot lose a result.
- **User SQL is not put through EF's raw-SQL formatting.** `ExecuteSqlRawAsync` parses its statement
  as a composite format string, so `INSERT INTO s VALUES (1, {'a': 1})` — an ordinary DuckDB struct
  literal — fails with `FormatException` before reaching the engine. The non-query path builds a
  command on the context's own connection instead, and `A_write_carrying_braces_reaches_the_engine_intact`
  is the regression guard.

## Row streaming, and why the cap does not apply here

`LakehouseOptions.MaxRowsPerResult` (default 10,000) exists because the HTTP path materialises rows
into a JSON response, and an unbounded result would be built in memory before anything was sent.

A wire connection has no such moment: each row is encoded to a `DataRow` and written to the socket as
the provider yields it. So this path streams rather than truncates, and `MaxRowsPerResult` is not
applied to it.

This is a deliberate, documented narrowing of invariant 6, and the reasoning matters more than the
exception. The invariant's purpose is *never materialise an unbounded result*, and streaming honours
that purpose exactly. Truncating instead would be worse than a slow query: a BI tool given 10,000 of
50,000 rows reports a confidently wrong number, with nothing on screen to say so. Cancellation,
statement timeouts, and early termination are preserved unchanged — closing the connection or
cancelling the request stops the scan.

`Lakehold:PgWire:MaxRows` can impose a ceiling anyway for operators who want one. Zero, the default,
means unbounded.

## Limits and hardening

The endpoint is a listening TCP port, so the limits are part of the design rather than tuning:

| Setting | Default | What it bounds |
|---|---|---|
| `MaxConnections` | 64 | Concurrent connections; further ones are refused rather than queued |
| `HandshakeTimeout` | 15s | How long an unauthenticated peer may take to complete startup |
| `IdleTimeout` | 30min | How long an established connection may sit between messages |
| `MaxMessageBytes` | 16 MB | Largest message from an authenticated client |
| — (fixed) | 8 KB | Largest message from a peer that has **not** authenticated |

Two of these exist because of specific attacks rather than tidiness. Without the handshake timeout, a
peer that opens a socket and sends nothing holds a connection slot indefinitely, and
`MaxConnections` silent sockets deny the endpoint to everyone at the cost of one TCP handshake each.
And because a message's length prefix sizes an allocation before its contents are read, an
unauthenticated peer that could name a large one turns `MaxConnections` sockets into gigabytes of
reachable allocation — hence a separate, much lower ceiling before authentication.

Both are covered by tests that assert the connection is closed rather than held.

## Observability

Alongside the query and session instruments every statement already records:

| Instrument | Why |
|---|---|
| `lakehold.pgwire.connections` | Open connections; against `MaxConnections` it says immediately whether the ceiling is why clients cannot connect, and a value that only rises means they are leaking |
| `lakehold.pgwire.connections.closed` | Tagged clean / dropped / refused / faulted, which separates a BI tool cycling its pool from clients being turned away |
| `lakehold.pgwire.auth.failures` | Its own instrument rather than a tag, because on a network-reachable port a rising rate is a security signal that wants its own alert |

## Session and concurrency model

A wire connection does **not** own a `Duckling`. It resolves one per statement through
`LakehouseService`, exactly as an HTTP request does, so:

- Idle eviction still works. A BI tool holding an idle connection open all afternoon does not pin a
  DuckDB instance.
- The session gate still serialises access (invariant 5). Two Power BI refreshes against one catalog
  queue rather than race.
- Query history and audit still record every statement, including the introspection ones — which is
  the first time BI traffic has been visible in Lakehold's own history at all.

The cost is that a connection carries no session state: temporary tables and `SET` values do not
survive between statements. For BI traffic, which is stateless read queries, this is invisible. It is
listed here because it will not be invisible to `psql` users.

## Catalog introspection

BI clients open by interrogating `pg_catalog`. DuckDB 1.5.4 implements enough of it to answer most of
that directly — `pg_catalog.pg_type` (39 rows), `pg_class` (47), plus `information_schema` — so
introspection is passed through to the engine rather than emulated.

A small shim answers the queries DuckDB has no equivalent for, principally `SHOW TRANSACTION
ISOLATION LEVEL` and the `SET` statements Npgsql issues on connect. The shim is a last resort and
every entry in it is a compatibility bug someone should eventually push upstream, not a design.

## Configuration

```jsonc
{
  "Lakehold": {
    "PgWire": {
      "Enabled": false,          // off by default: this is a database port
      "Port": 5433,
      "MaxRows": 0,              // 0 = unbounded, streamed
      "AllowCleartextPassword": false,
      "AllowAnonymous": false    // refuses to start without a password unless set
    }
  }
}
```

The password is a secret and lives in `.env` as `Lakehold__PgWire__Password`, never in
`appsettings*.json`.

## Verification

`tests/Lakehold.Api.Tests/PgWire*` covers the codec directly and drives a live endpoint with
**Npgsql** — the same driver Power BI's PostgreSQL connector is built on, so the test exercises a
real client's message sequence rather than a hand-rolled one. The whole stack underneath is real: a
DuckLake catalog on disk, the control plane resolving the tenant, the session pool serving the
statement.

Established by that suite rather than assumed:

- Npgsql completes the handshake, MD5 authentication, and the extended-query sequence.
- Columns arrive as `long`, `string`, `double`, and `bool` rather than as text the client would have
  to reinterpret — the failure that leaves a BI tool charting strings.
- `NULL` arrives as null rather than as an empty string, which is what writing a zero-length field
  instead of the `-1` sentinel produces.
- Decimals — including negative, zero, and multi-group values — and timestamps survive the binary
  encoding. These are the encoders most likely to be silently wrong: a mis-set base-10000 weight
  renders 1.5 as 15000, and a wrong epoch lands a timestamp in 1970 while still parsing cleanly.
- A result of 10,500 rows arrives complete, past the 10,000-row HTTP ceiling.
- A bad password is refused, an unknown catalog returns `3D000`, and DuckDB's own error text reaches
  the client.
- Every statement lands in query history — the point of entering through `LakehouseService`.

## Power BI does not work yet, and here is exactly why

This was an open risk when the endpoint shipped. It has since been measured, and the answer is no.

**Clients that perform full type-catalog loading cannot connect.** The tests pass because they set
`Server Compatibility Mode=NoTypeLoading`; Power BI has no such option, and it bundles its own
Npgsql — Microsoft's connector documentation puts recent builds on **Npgsql 4.0.17**, far older than
the 10.x the suite exercises.

Npgsql's type loader reads the backend catalogue with a query that inner-joins `pg_proc` on the
receive function of each type:

```sql
FROM pg_type AS a
JOIN pg_namespace AS ns ON ns.oid = a.typnamespace
JOIN pg_proc      AS p  ON p.oid  = a.typreceive
```

Against DuckDB 1.5.4 that join returns **zero rows**, and the reason is specific:

| Probe | Result |
|---|---|
| `pg_catalog.pg_type` rows | 39 |
| Joined to `pg_namespace` only | 39 ✅ |
| `typreceive IS NULL` | **39 of 39** |
| Joined to `pg_proc` on `typreceive` | **0** ❌ |
| `pg_proc` rows named `array_recv` | **0** |

`typreceive`, `typelem`, and `typarray` are present as columns but NULL for every type, so the join
matches nothing and the client ends up with an empty type catalogue. `array_recv` — which Npgsql uses
to recognise array types — does not exist in DuckDB's `pg_proc` either.

So the failure is not a subtle incompatibility to be chased through a driver's logs. It is one
missing column's worth of data, and it stops every full-type-loading client at connection time.

### What the handshake actually sends

Captured from a live connection rather than reasoned about, by reading the statements the endpoint
recorded in query history while a type-loading client connected. The load arrives as **four
statements in a single simple-query message**:

1. `SELECT version()`
2. the type catalogue — a nested query over `pg_type`, `pg_class`, `pg_proc`, `pg_range`, `pg_namespace`
3. composite type fields, over `pg_attribute`
4. enum labels, over `pg_enum`

Two corrections to what this document previously said, both of which matter:

- **Multi-statement support is the first blocker, not the type catalogue.** A server that runs the
  body as one statement answers with one result set where the client expects four, and the
  connection dies during handshake with `Received backend message ReadyForQuery while expecting
  RowDescriptionMessage`. That is now fixed — see below.
- **The modern loader uses `LEFT JOIN pg_proc`, not an inner join.** The NULL `typreceive` finding
  above is real but is not fatal to it; it is fatal to older Npgsql versions that inner-join, which
  is what Power BI is likely to bundle.

### Fixed: multi-statement simple queries

`PgStatementSplitter` splits a simple-query body into statements, answering each with its own result
set and sending one `ReadyForQuery` at the end, as the protocol requires. A failing statement
abandons the rest of the message, as PostgreSQL does.

The split is lexical, not a parse: it skips string literals, quoted identifiers, dollar-quoted
bodies, and both comment styles, purely to know when a semicolon is *inside* something. Nothing about
a statement's meaning is inspected, so this does not become the SQL-parsing security boundary that
invariant 4 rules out. It also fixes `psql` users sending several statements at once, which was
broken for the same reason.

### Still open: the catalogue itself

Getting a type-loading client all the way through needs three more things, each established by
experiment and each with a known remedy:

| Obstacle | Established by | Remedy |
|---|---|---|
| `pg_range` does not exist in DuckDB | `Catalog Error: Table with name pg_range does not exist!` | Supply it as an empty CTE prefixed to the statement — a CTE shadows a table name, so the client's own query then runs against DuckDB's real `pg_type` |
| 22 of 39 types have a NULL `oid` | `SELECT oid, typname FROM pg_type` | Filter them out: a type with no OID cannot be named on this wire |
| `pg_namespace.oid` does not match `pg_type.typnamespace` inside a DuckLake session | The client's inner join returned **0 rows** in-session but 17 from the CLI | Derive the namespace from `pg_type` itself so the join is self-consistent wherever the session points |

With all three applied the catalogue query returns the right 17 rows — `pg_catalog|20|int8|b`, and so
on — and the client gets further into `LoadBackendTypes` before failing on a NULL where it wants a
non-nullable string. That last one is unresolved.

**None of this is in the shipped code.** The mechanism was built, measured, and then reverted rather
than left half-finished: it rewrites any statement mentioning `pg_type`, including a user's own, and
until it carries a client all the way through it would be a behaviour change with no benefit. The
findings are recorded here so the work resumes from them instead of rediscovering them.

The tension worth restating: this is a bigger shim than "a last resort" implies. The justification is
that these statements are a driver handshake rather than a user's query and return no user data — but
if finishing it means growing a synthetic catalogue that must track DuckDB's, that trade should be
re-examined rather than assumed.

### Status by client

| Client | Status |
|---|---|
| Npgsql 10.x with `NoTypeLoading` | ✅ Verified by the test suite |
| `psql` | ✅ Simple query protocol covered by a raw-socket test |
| Power BI | ❌ Blocked on type loading, above |
| Npgsql without `NoTypeLoading` | ❌ Same blocker |
| Tableau, Metabase, DBeaver | ⚠️ Untested; those on libpq rather than Npgsql may be unaffected |

**A client that does connect still needs one setting changed.** Power BI's PostgreSQL connector
enables *Use Encrypted Connection* by default and this endpoint declines TLS, so that box has to be
cleared even once type loading is solved.
