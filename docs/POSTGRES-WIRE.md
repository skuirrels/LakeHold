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

## Authentication, and its honest limits

The API has no authentication (see [`ARCHITECTURE.md`](ARCHITECTURE.md)), so this endpoint cannot
inherit one. It implements the protocol's own password exchange against a **single shared secret**
from configuration:

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

**TLS is not implemented.** `SSLRequest` is answered `N` and the session continues unencrypted, which
is what `psql` and Npgsql do by default when the server declines. Terminate TLS in front of the port,
or keep it on a trusted network.

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
| `Query` (simple) | → | The body is passed to the engine unchanged; it is not split on `;` |
| `Parse`, `Bind`, `Describe`, `Execute`, `Sync`, `Close`, `Flush` | → | Extended query protocol |
| `RowDescription`, `DataRow`, `CommandComplete`, `EmptyQueryResponse` | ← | |
| `ParseComplete`, `BindComplete`, `NoData`, `ParameterDescription`, `PortalSuspended` | ← | |
| `ErrorResponse`, `NoticeResponse` | ← | Engine errors are returned verbatim, as the HTTP API does |
| `Terminate` | → | |

Deliberately not implemented:

- **Bound parameters.** A `Bind` carrying parameter values is refused with an error rather than
  guessed at. Textual substitution would be a SQL-injection surface inside the tenant's own session
  and a silent correctness risk; the provider's parameterised path is the correct fix and is the
  first follow-up.
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

### Not yet verified

**Power BI itself has not been driven against this endpoint.** Npgsql is what its connector is built
on, which is why the suite uses it, but the connector's own introspection queries and Navigator
behaviour are a layer above that and remain untested. The known risk is Npgsql's type-catalog
loading: the tests connect with `Server Compatibility Mode=NoTypeLoading`, and whether a client that
performs full type loading finds enough of `pg_type` in DuckDB is an open question. Tableau,
Metabase, and DBeaver are equally untested.
