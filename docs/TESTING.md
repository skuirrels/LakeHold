# Testing LakeHold

LakeHold uses three layers of automated evidence. A change is not “covered” merely because one
layer is green: each catches a different class of failure.

| Layer              | What it proves                                                                            | Command                                                                                                 |
| ------------------ | ----------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------- |
| Unit and component | Pure rules, API request construction, credential handling, and Angular state/rendering    | `dotnet test Lakehold.slnx` and `npm run test:unit --prefix web/lakehold-ui`                            |
| Integration        | Real PostgreSQL control-plane migrations, PostgreSQL DuckLake metadata, and S3-compatible storage | Start PostgreSQL and MinIO, then `dotnet test Lakehold.slnx` |
| End to end         | Routes, proxying, live API calls, workbench interaction, and rendered results in Chromium | Start the complete Compose stack, then `npm run test:e2e --prefix web/lakehold-ui`                      |

## Complete local run

The canonical full-suite command is:

```bash
make test
```

It restores and builds both applications, runs every backend test with disposable PostgreSQL and
S3-compatible services, rejects skipped backend tests, runs the frontend unit suite and production
build, runs the normal Chromium journeys against a fresh seeded production-shaped node, and then
runs a separate authentication-required demo journey before the destructive Phase 2 operator
journey. Its Compose projects, host ports, networks, and volumes are isolated from the development
and production stacks and removed on exit.

The individual commands remain useful for focused work. Run the isolated suites with:

```bash
dotnet test Lakehold.slnx
npm run test:unit --prefix web/lakehold-ui
npm run build --prefix web/lakehold-ui
```

Run every live-service integration:

```bash
docker compose up -d postgres minio
docker compose up minio-bucket
dotnet test tests/Lakehold.Engine.Tests/Lakehold.Engine.Tests.csproj
```

The engine result must report **zero skipped tests**. A green run with skipped PostgreSQL or S3
cases is the isolated suite, not the integration suite.

Run the browser journeys:

```bash
docker compose up -d
npm run test:e2e:install --prefix web/lakehold-ui
npm run test:e2e --prefix web/lakehold-ui
```

Set `LAKEHOLD_E2E_BASE_URL` to test another running deployment. Browser traces, screenshots, video,
and the HTML report are written under `output/playwright/` and are retained only as local or CI
evidence.

Run the destructive full-system operator simulation:

```bash
npm run test:e2e:phase2 --prefix web/lakehold-ui
```

That command builds a production-shaped stack under the separate `lakehold-phase2` Compose project,
uses dedicated host ports, starts from an empty authentication-required state volume, and removes
all of its containers and volumes on exit. It is the only browser suite permitted to apply expiry,
cleanup, or restore.

## Coverage matrix

| Product surface                                   |      Unit/component |                              Integration |                           Browser journey |
| ------------------------------------------------- | ------------------: | ---------------------------------------: | ----------------------------------------: |
| Tenant, catalog, and token provisioning           |                 Yes |                            API test host |                       First-run component |
| Authentication, role policy, and tenant isolation |                 Yes |   Read-only Duckling and PostgreSQL wire | Credential and anonymous request boundary |
| SQL execution, result types, row limits, errors   |                 Yes |                     Live DuckDB/DuckLake |                Run, render, fail, recover |
| Catalog schemas and table discovery               |                 Yes |                             Live catalog |                   Filter, insert SQL, run |
| Query history                                     |                 Yes |                            API test host |                       Replay a live query |
| Data history, snapshot drill-down, and time travel |                 Yes |                            Live DuckLake | Browse, compare, plan/confirm restore |
| Storage rollups and file inventory                |                 Yes |                Local, PostgreSQL, and S3 |                             Storage panel |
| Flush and compaction                              |                 Yes |                            Live DuckLake |                         Operator controls |
| Expiry and orphan cleanup safety                  |                 Yes |                            Live DuckLake |                        Dry run and cancel |
| Backup and restore                                |                 Yes |                Local, PostgreSQL, and S3 |                     Backup/restore panels |
| Eject and verification                            |                 Yes |         Live DuckLake and Parquet reader |                               Eject panel |
| Change feed and webhook subscriptions             |                 Yes | Live DuckLake and dispatcher test server |                             Changes panel |
| Scheduled maintenance visibility                  |                 Yes |                            API test host |                            Schedule panel |
| MCP tools and resources                           |                 Yes |             In-process MCP client/server |                     Not a browser surface |
| PostgreSQL wire endpoint                          | Protocol and policy |                     Npgsql client/server |                     Not a browser surface |
| Public site, docs, comparison, provider pages     |       Angular build |                           Not applicable |                  Route and heading checks |

## `/compare` claim coverage

`e2e/compare-capabilities.spec.ts` is the guardrail for the comparison page. It reads the rendered
LakeHold column and requires an exact match with `e2e/support/compare-capabilities.ts`, including
the tone assigned to every row. A new or reworded claim therefore fails until it names current test
evidence. The same test also follows the decision guidance and workbench call to action.

The contract deliberately distinguishes a simulated capability from a deployment fact, a declared
limitation, and roadmap work. A planned or unavailable feature must stay labelled that way; it
cannot satisfy the contract by pointing at an unrelated passing test.

| LakeHold comparison row       | Automated proof                                                                 |
| ----------------------------- | ------------------------------------------------------------------------------- |
| Deployment                    | Phase 2 builds, boots, operates, and removes a production-shaped local node     |
| Where your data lives         | Phase 2 local state plus opted-in live S3 integration                           |
| Accounts, SSO, permissions    | Phase 2 owner/editor/reader boundaries plus OIDC principal tests                |
| Table format                  | DuckLake execution, independent Parquet reads, and PostgreSQL metadata restore  |
| Read data without the product | Eject rows and manifest counts verified by a reader with no DuckLake extension  |
| Other engines read it live    | Eject runs now; Iceberg REST remains an explicitly tested roadmap boundary      |
| Time travel                   | Phase 2 version query and restore plus backup/history round trip                |
| Verified, signed export       | Phase 2 signed eject plus independent signature/tamper verification             |
| Change data capture           | Phase 2 typed pull feed and signed webhook retry plus all change image types     |
| AI / MCP                      | Phase 2 authenticated reads and two-gate writes plus disabled-mode tests        |
| BI tools                      | Real `psql`, Npgsql type/result tests, and explicit Power BI limitation         |
| Maintenance control           | Browser dry-run first, then explicit apply on disposable state                  |
| .NET / EF Core                | Live LakeContext execution/instrumentation; client package remains qualified    |
| Scale ceiling                 | Single-node/single-session implementation boundary, stated as a limitation      |
| Concurrent writers            | A deterministic two-caller serialization test for the catalog gate             |
| Operational burden            | The operator-owned Compose lifecycle itself is exercised                       |
| Licence                       | Repository licence contract                                                     |
| Cost shape                    | Self-hosted deployment topology contract, not a fabricated price assertion      |

## Test design rules

- A service-backed test must skip only when its opt-in variable is absent. CI supplies every
  variable and treats any skip as incomplete verification.
- Normal E2E tests may mutate only the seeded demo catalog or data they create themselves.
  Destructive maintenance is asserted at the dry-run boundary there. Applied expiry, cleanup, and
  restore belong only in the Phase 2 disposable stack.
- Prefer accessible roles, labels, and visible behavior in browser assertions. Use CSS locators only
  for structural elements that have no user-facing role.
- Keep generated browser evidence out of source control.
- A test must fail when the behavior it protects is broken. Compilation and count growth are not
  substitutes for a meaningful assertion.

## Phase 2: disposable full-system simulations

`e2e/operator-simulation.spec.ts` is the safe real-user simulation against the normal development
node. It inspects physical files and the change feed, reviews and cancels an atomic table-data restore,
performs safe maintenance, writes and lists a backup, creates and opens a verified eject, and inspects
scheduled-run visibility.

`e2e/phase2-operator.spec.ts` runs only through `scripts/test-phase2.sh` against disposable state. It
boots an empty authentication-required node, provisions the first tenant and catalog, adopts the
one-time owner token, and then proves:

- invalid, expired, and revoked tokens are refused, including revocation through a real `psql`
  connection;
- MCP 2026 discovery, exact tool inventory, and an authenticated query work over the external HTTP
  endpoint, while an opted-in `execute` tool still requires a write-capable credential and the
  `query` tool remains read-only;
- owner, editor, and reader roles have distinct live capabilities, including revocation through a
  real `psql` connection;
- the typed change feed is readable and a signed webhook survives a forced 503 retry with stable
  delivery identity and cursor advancement;
- an earlier snapshot can be queried directly and restored, backup restore rebuilds a new metadata
  file, and a second restore refuses to overwrite it;
- expiry and orphan cleanup can be dry-run and then applied, eject is row-count verified and signed,
  and final rows, backups, and eject manifests agree through independent API and PostgreSQL-wire
  reads.

The remaining Phase 2 expansion scenarios are:

- repeat backup, restore, eject, and storage inspection with PostgreSQL metadata and S3 data through
  the user-facing APIs;
- run two tenants and concurrent operators to prove isolation, queueing, cancellation, and session
  eviction under contention.
