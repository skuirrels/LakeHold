# Enterprise data platform review — August 2026

An assessment of LakeHold against the "enterprise data platform" category, from three angles: the
market it would enter, the capabilities it would need, and the functional demand it would be scored
against.

[`ARCHITECTURE.md`](ARCHITECTURE.md) states LakeHold's *current* position — "self-hostable
open-format lakehouse", with the defensible wedge named explicitly.
[`COMPETITIVE-RESEARCH.md`](COMPETITIVE-RESEARCH.md) is the dated evidence behind it. This document
asks a different question: **what would change if LakeHold aimed one category up, and is that the
right aim?** It is a review, not a decision, and nothing in it supersedes the position in
`ARCHITECTURE.md` until someone chooses to act on it.

## Method and its limits

Stated first, on the same principle as `COMPETITIVE-RESEARCH.md`.

- **Capability claims were verified against the code**, not against documentation. Where this
  document says something is absent, that means a search of `src/` returned nothing, and the search
  is named so it can be re-run.
- **Market and demand claims lean on [`COMPETITIVE-RESEARCH.md`](COMPETITIVE-RESEARCH.md)**, gathered
  26 July 2026. Its counts are a floor and its sampling limits are its own. Nothing here re-gathers
  that evidence; where this document cites a competitor date, the citation is to that snapshot.
- **No customer or prospect input.** There is no win/loss data, no evaluation matrix from a real
  buyer, and no pipeline to reason from. The demand section reasons from the upstream trackers and
  from standard evaluation checklists, which is weaker evidence than a lost deal.
- **No commercial model was reviewed** because none exists in the repository to review.

Verified against the tree as of this branch's base commit, and **re-verified on 3 August 2026**
against `main` after the v1.3.0 connector platform and the v1.4.0 `/api/v1` contract and SDK
candidates landed. Claims this document originally made about ingestion, client SDKs, lineage, and
data quality were invalidated by that work and have been corrected in place rather than left
standing; the corrections are marked where they occur.

This document assesses the *category claim*. [`ENTERPRISE-DATA-PLATFORM.md`](ENTERPRISE-DATA-PLATFORM.md)
states the position LakeHold has since adopted, and
[`ENTERPRISE-DATA-PLATFORM-ROADMAP.md`](ENTERPRISE-DATA-PLATFORM-ROADMAP.md) sequences the work.
Read those first: where this review and they disagree on what is built, they are the current record
and this is the argument about whether the category is the right one to claim.

## Conclusion

**The capability gap is real but not the binding constraint. Category selection is.**

LakeHold scores strongly on roughly ten rows of a forty-row enterprise evaluation — openness,
verified exit, self-hosting, CDC, maintenance control, .NET integration, MCP authentication, cost
shape, and, since v1.3.0 and v1.4.0, managed ingestion and a versioned public API with four client
SDKs. It still returns "not built" on transformation and orchestration, the semantic layer,
table-to-table lineage, data quality outside the ingestion boundary, fine-grained security, and
elastic scale-out. Entering that evaluation invites scoring on the rows where nothing exists.

*Re-verified 3 August 2026.* The connector and SDK work closed the two largest gaps this review
originally identified, which strengthens the capability position without changing the argument
below: the constraint was never the count of built rows.

The same organisation asking a narrower question — *"a governed Parquet lakehouse inside our own
VPC, queryable natively from .NET services, with provable exit"* — runs a five-row matrix that
LakeHold wins outright today.

The recommendation is therefore to **adopt the engineering discipline an enterprise claim implies
without adopting the category label**: build the execution seam, close the isolation gates, ship the
interoperability surface. Those are worth doing on their own merits. The label can follow the
capability, and it costs more than engineering to earn.

## 1. Market perspective

### The category is the most defended real estate in data infrastructure

"Enterprise data platform" is occupied by Databricks, Snowflake, Microsoft Fabric, Palantir Foundry,
Cloudera, and Dremio. Entry is not by claiming the label; it is by clearing a procurement bar. The
label is an acceptance standard, not a description — and the acceptance standard is set by the
incumbents, not by the entrant.

### The move would trade a wedge for a frontal position

[`ARCHITECTURE.md`](ARCHITECTURE.md) names the intersection precisely: no competitor holds all three
of *{runs entirely in your infra, table data readable with no vendor catalog, .NET/EF Core model
integration}*. That is a genuine wedge, and it is small enough to defend with the engineering
capacity this project actually has.

"Enterprise data platform" is not a wedge. It is a claim against organisations with orders of
magnitude more R&D, on the axes where they are strongest — scale, governance breadth, connector
count. It also de-emphasises the .NET integration story, which is the one axis where the incumbents
are *structurally* unable to compete: a JVM lakehouse cannot share an EF Core model with the
application that queries it, ever. That is not a feature gap they can close with a sprint.

### The buyer changes, and the go-to-market does not follow

| | Current position | Enterprise position |
|---|---|---|
| Buyer | Platform engineer, .NET team lead | CDO, enterprise architect |
| Motion | Bottom-up, self-serve, Apache-2.0 | Top-down, RFP, procurement |
| Proof required | It runs; the tests pass | SOC 2, pen test, SLA, references |
| What is bought | Code | Support, indemnity, roadmap commitments |
| Sales cycle | Days | Two to four quarters |

The repository has no commercial entity, no pricing, no support tier, and no compliance artifacts.
`README.md` describes the cost shape as "a VM and a bucket". **An enterprise data platform with no
commercial model is a contradiction**: enterprise buyers do not buy code, they buy someone to call
during an incident and a contract that assigns liability.

The instructive datapoint is already in [`COMPETITIVE-RESEARCH.md`](COMPETITIVE-RESEARCH.md) §3:
MotherDuck spent H1 2026 shipping SOC 2 Type II, GDPR, tiered support, and EU/APAC regions. That is
what the label costs in practice, and almost none of it is engineering work.

### Where an enterprise claim is genuinely available

There is a segment the incumbents structurally cannot serve: **sovereignty, air-gap, and regulated
on-premise**. Snowflake has no on-premise story; Databricks retains a vendor-hosted control plane
even when compute runs in the customer's VPC. For a defence supplier, a national health system, or a
bank under data-residency obligation, self-hosting is a *requirement* rather than a preference, and
running entirely inside the customer's environment is worth more than elastic scale.

This is the strongest available market case for aiming higher, and it is a *segment* argument rather
than a *category* argument. The tension to note honestly: that segment demands the most compliance
evidence of any, and compliance evidence is what the project has least of.

### Timing: the category is being reshaped by agents

[`COMPETITIVE-RESEARCH.md`](COMPETITIVE-RESEARCH.md) §4 records Snowflake and Databricks converging
on four bets in the same quarter — agents as the primary compute unit, MCP as the protocol layer, a
semantic layer serving certified meaning to those agents, and governance extended to AI systems.
When two competitors who agree on nothing else ship the same architecture simultaneously, that is
the category moving.

An enterprise data platform in late 2026 is judged on the agentic question. LakeHold has a shipped
answer that is unusually strong and unusually specific: **capability is enforced as attachment**
(invariants 4 and 20), so a read-only agent token yields a read-only *attachment* and a write fails
in the engine rather than in a policy check an agent might route around. Given the practitioner
complaint recorded in the same research — that MCP servers differ most in *how they authenticate* —
that is a sharper claim than "enterprise", and it is true today. See [`MCP.md`](MCP.md).

## 2. Strategic and capability perspective

Each row below was checked against the code rather than the documentation.

| Enterprise commitment | Status in `src/` | Evidence |
|---|---|---|
| Pluggable execution workers | **Absent.** No executor seam exists | [`Duckling.cs`](../src/Lakehold.Engine/Execution/Duckling.cs) is DuckDB directly |
| Workload queues, admission control, quotas | **Absent.** Cancellation only | No match for quota/admission/rate-limit in `src/` |
| Multi-engine table/catalog interoperability | **Absent** | One code comment mentions Iceberg; no endpoint |
| SSO, RBAC, fine-grained data policy | **Partial.** Three flat roles, no row/column policy | [`TokenRole.cs`](../src/Lakehold.ControlPlane/Model/TokenRole.cs) |
| Immutable audit, lineage, policy records | **Partial.** Audit yes; connector-run lineage yes; immutability and table-to-table lineage no | `ConnectorRun` lineage in `Entities.cs` |
| Tested backup, PITR, DR, controlled upgrades | **Partial.** Documented; control-plane restore absent | [Roadmap](PRODUCTION-READINESS-ROADMAP.md) Phase 5 |
| Multi-tenant isolation under untrusted SQL | **Open release blocker** | [Roadmap](PRODUCTION-READINESS-ROADMAP.md) Phases 1 and 3 |
| Published SLOs, capacity metrics, diagnostics | **Partial.** Telemetry yes; SLOs undefined | [`OPERATIONS.md`](OPERATIONS.md) |
| Production PostgreSQL wire, JDBC/ODBC, BI | **Partial.** PG wire ships; Power BI blocked | Type-catalogue shim outstanding |

### Compute pluggability has no seam

The principle that one embedded engine should not become the architectural ceiling is sound. The
implementation does not exist. There is no `IQueryExecutor`, no worker abstraction, and no dialect
boundary. [`Duckling`](../src/Lakehold.Engine/Execution/Duckling.cs) *is* DuckDB: it owns a
DuckDB-backed `LakeContext`, calls `MemoryLimit()` and `Threads()` on the connection directly, and
serialises every operation through a single non-reentrant gate:

```csharp
private readonly SemaphoreSlim _gate = new(1, 1);   // Duckling.cs:36
```

Invariant 5 deliberately preserves that gate until a per-tenant read pool replaces it, and
[`PROVIDER-FEEDBACK.md`](PROVIDER-FEEDBACK.md) measures the case for doing so. That is a coherent
design. What it is not is a pluggable execution boundary, and any external claim of one would fail a
technical due-diligence call.

**This is the most actionable finding in the review.** The abstraction is worth building
independently of the positioning question: it is what makes the compute ceiling a bounded
engineering problem rather than an identity. Build the seam before claiming pluggability.

### There is no workload management of any kind

Searching `src/` for quota, admission, rate-limit, or concurrency-limit returns nothing. What exists
is per-session, static, and global, in
[`LakehouseOptions.cs`](../src/Lakehold.Engine/Configuration/LakehouseOptions.cs):

| Control | Default | Scope |
|---|---|---|
| `MemoryLimit` | `2GB` | Per session, fixed |
| `Threads` | `4` | Per session, fixed |
| `MaxRowsPerResult` | `10_000` | Materialising paths (invariant 6) |
| `StatementTimeout` | 2 minutes | Per statement |

Cancellation is real and threaded end to end, which is more than many products manage. Everything
else is absent, and critically there is **no aggregate ceiling** — N warm sessions each claim 2 GB
and 4 threads independently, so per-session limits do not compose into a node-level guarantee. The
roadmap already identifies this in Phase 3 ("aggregate admission control so per-session memory and
thread limits cannot collectively exhaust a node") and it is open.

The practical consequence: one tenant's expensive aggregation degrades every other tenant on the
node, and nothing in the product observes it. That is the noisy-neighbour problem, and it is the
gap most likely to surface in a shared-tenancy evaluation.

### The interoperability surface is unbuilt, and it gates everything else

Searching `src/` for `iceberg` returns exactly one hit — a comment in
[`LakehouseOptions.cs:130`](../src/Lakehold.Engine/Configuration/LakehouseOptions.cs) about file-size
conventions. There is no Iceberg REST endpoint, no metadata translation, and no engine bridge. It is
USP 5 in [`ARCHITECTURE.md`](ARCHITECTURE.md), marked not built.

The strategic case for it is strong and is made in two places already: IRC is the universal catalog
protocol, and upstream issue #37 ("Export an Iceberg metadata snapshot", 17 👍) shows the demand
exists inside the DuckLake community itself. Nothing serves *live* DuckLake as IRC.

The sequencing point is the one worth recording here: **an enterprise position is predicated on this
surface**. Every enterprise conversation an upmarket framing wins will arrive at "can Spark write to
it?", and eject is not an answer to that question. Positioning ahead of the build inverts the risk.

### Governance is well-built but shallow

The authorization design is the strongest code in the repository.
[`CapabilityPolicy`](../src/Lakehold.ControlPlane/Security/Capability.cs) is transport-neutral by
construction, checks subject before capability so an unreachable tenant returns **404 rather than
403**, and orders `CapabilityOutcome` so that a default-constructed decision refuses rather than
allows. That is careful security engineering and it should be said plainly.

The gap is depth, not quality:

- **Three flat roles** — Owner, Editor, Reader. No groups, no attribute-based policy, no custom
  roles, no delegated administration below the tenant.
- **No row- or column-level security.** [`ARCHITECTURE.md`](ARCHITECTURE.md) marks it "later".
  `COMPETITIVE-RESEARCH.md` §3 found MotherDuck has *deliberately declined* to build it — but
  Databricks, Snowflake, and Dremio all ship it, and those are the competitors an upmarket move
  selects.
- **`Lakehold:Auth:RequireAuthentication` defaults to `false`.** For a self-hosted tool whose
  production Compose file sets it true, that is a defensible development convenience, and both
  `README.md` and the feature matrix disclose it. For anything claiming enterprise grade, an
  authentication switch that defaults open is indefensible. Roadmap Phase 2 treats it as a release
  blocker and it is open.
- **Lineage is partial and audit is not tamper-evident.** *Corrected 3 August 2026:* the connector
  platform records a durable source-to-table edge and per-refresh run lineage with quality evidence,
  so the original "no lineage" reading is wrong. What is still absent is table-to-table and
  column-level lineage across transformations, and an append-only audit store.

### Multi-tenancy is not yet safe for untrusted tenants

This is the largest capability gap behind the label, and it is already documented rather than
discovered here. [`PRODUCTION-READINESS-ROADMAP.md`](PRODUCTION-READINESS-ROADMAP.md) Phases 1 and 3
are both marked release blockers and both open. Phase 3 is the serious one: arbitrary DuckDB SQL is
not contained, so `ATTACH`, `COPY`, `read_parquet`, `glob`, secret creation, extension installation,
and outbound network access are all reachable from tenant SQL. The roadmap correctly refuses to
treat keyword parsing as a boundary, consistent with invariant 4.

The roadmap's own conclusion is unambiguous: LakeHold "should not be represented or deployed as a
secure shared multi-tenant service until the isolation and production-security gates below are
complete."

An enterprise data platform is multi-tenant by definition. **The category requires a gate the
project's own roadmap says is not yet passed**, and no amount of positioning changes that ordering.

### Recovery and upgrade are documented, not implemented

Phase 5 remains open on exactly the items a buyer asks about first: no control-plane backup and
restore (tenants, tokens, subscriptions, audit state), backups not guaranteed outside the primary
failure domain, no automated restore drill, and no defined RPO or RTO. Enterprise procurement asks
for recovery-point and recovery-time objectives in the first technical meeting.

Schema upgrades still run on `EnsureCreated` plus open-ended additive repair (Phase 7). That is a
development-stage mechanism, and it is not one an enterprise can accept for a system holding its
catalog metadata.

### What an enterprise framing under-weights

Four capabilities are shipped, tested, and genuinely rare. Each receives less emphasis in an
enterprise framing than it deserves, because none of them is a row on a standard evaluation matrix:

1. **Verified, signed eject bundles.** The manifest is written last and only after every table's
   re-read row count matches the catalog (invariants 15 and 16). The feature matrix marks this
   **unique** — no competitor offers it. In a sovereignty sale, a signed and dated attestation that
   the customer can leave is an extraordinarily strong artifact, and it is *more* persuasive to a
   regulated buyer than most of the rows an enterprise matrix contains.
2. **CDC with no Debezium and no Kafka.** Also marked unique: at-least-once delivery with a
   resumable cursor (invariant 18) and signed webhooks, with no separate pipeline to operate.
3. **MCP with capability enforced as attachment.** The differentiator the competitive research
   identifies as the one that actually distinguishes MCP servers.
4. **Maintenance safety.** Dry-run by default, leased, and scheduled — sitting directly on the
   loudest unmet operational need in the DuckLake community. `COMPETITIVE-RESEARCH.md` §5 records
   the cluster: orphaned files from failed inserts, unbounded `LIST` timeouts during orphan cleanup,
   `CHECKPOINT` deleting catalog-active files, and deleted rows reappearing where inlined tombstones
   and Parquet overlap. Several of those upstream bugs are the same failure class invariants 12 and
   15 exist to prevent.

All four are narrow, defensible, provable with tests, and unavailable elsewhere. An enterprise
framing spends attention on axes where the honest answer is "not yet" instead of axes where the
answer is "uniquely, and here is the test that proves it."

## 3. Functional demand perspective

### The demand evidence already gathered points somewhere else

[`COMPETITIVE-RESEARCH.md`](COMPETITIVE-RESEARCH.md) §5 ranks demand from the `duckdb/ducklake`
trackers by reaction count. Cross-referenced against what LakeHold ships:

| Ask | 👍 | LakeHold status |
|---|---|---|
| Partitioning at `CREATE TABLE` | 26 | **Reported, not managed** — [`TableInspector`](../src/Lakehold.Engine/Catalog/TableInspector.cs) reads `ducklake_partition_info`; no management API |
| Export an Iceberg metadata snapshot | 17 | Not built (USP 5) |
| Git-style branching | 12 | Roadmap |
| Per-table snapshot expiry | 11 | Not built |
| `target_file_size` with partitioning | 11 | Not built |

Partitioning leads the next item by a factor of 2.4, and #224 is the same complaint from the other
side — partitioning that exists but cannot be sized. **Physical layout control is the loudest unmet
need in the ecosystem LakeHold occupies, and LakeHold currently displays it without letting anyone
change it.** The table inspector is a genuinely good surface; it makes the maintenance controls
*decidable* rather than a guess, and it stops one step short of the action users are asking for.

Not one item on this list appears on an enterprise gate checklist, which is populated by HA, SSO,
RBAC, and audit. Both sets of needs are real. Only one is evidenced by users the product can reach
today, and closing the gap between "we show it" and "you can set it" is small work against the
largest counted demand signal available.

### The functional gaps a buyer names first

In roughly the order they get raised in an evaluation:

1. **Ingestion — largely closed.** *Corrected 3 August 2026.* This was the review's strongest
   original criticism and the connector platform has answered most of it: managed full-snapshot and
   incremental connectors with PostgreSQL, REST, gRPC, and HubSpot sources, scheduled runs,
   checkpoints, secret providers, and per-run lineage and quality evidence
   ([`CONNECTORS.md`](CONNECTORS.md)). The residual gap is breadth of source coverage and streaming
   ingestion, not the absence of an ingestion story. Browser CSV/XLSX upload remains beside it as a
   workbench convenience.
2. **Transformation and orchestration.** No dbt integration, no DAG, no materialisations, no
   dependency graph. Saved queries with explicit publication as DuckLake views is a good primitive
   and roughly five percent of the expected surface. Worth noting that MotherDuck reached dbt Cloud
   *through their PostgreSQL endpoint* — a path LakeHold already has the surface for.
3. **Semantic layer.** `COMPETITIVE-RESEARCH.md` is emphatic that this has stopped being a BI nicety
   and become the substrate agents consume. It sits in "Later" on the roadmap. Generating it from
   the EF Core model is something no JVM lakehouse can do, and it compounds with the MCP server
   rather than competing for the same slot. **Currently the most under-prioritised item relative to
   its strategic value.**
4. **Data quality and observability.** *Corrected 3 August 2026:* connector refreshes now carry
   quality gates and durable evidence, so quality is no longer absent at the ingestion boundary.
   It remains absent everywhere else — no expectations or freshness checks on tables the platform
   did not ingest, and no anomaly detection. The survey signal in §5 names silent data-quality
   issues persisting for days as a recurring complaint.
5. **Lineage.** *Corrected 3 August 2026:* present for connector runs, absent across
   transformations. A buyer asking "where did this column come from" still has no answer once the
   data has been through a query.
6. **BI reality.** The PostgreSQL wire endpoint is valuable and correctly framed as parity rather
   than differentiation. Power BI remains blocked on type-catalogue loading — and Power BI is the
   default BI tool in precisely the .NET-shop segment that is LakeHold's best-fit audience. **This
   is the highest-leverage functional gap in the product**: it sits on the intersection of the
   existing wedge and an existing surface, and it is a shim rather than a subsystem.

## Recommendations

Recommendations, not commitments — each names the finding that produced it.

> **Status, 3 August 2026.** The first recommendation below has been overtaken: LakeHold has
> adopted the Enterprise Data Platform category in
> [`ENTERPRISE-DATA-PLATFORM.md`](ENTERPRISE-DATA-PLATFORM.md), with a staged plan in
> [`ENTERPRISE-DATA-PLATFORM-ROADMAP.md`](ENTERPRISE-DATA-PLATFORM-ROADMAP.md). That document scopes
> the claim more narrowly than this review feared — "a smaller, private platform for .NET and lean
> data teams", with an explicit current-boundary note naming what is not yet built — which answers
> the substance of the objection even though it keeps the label. The recommendations are retained
> unedited as the record of the argument, not as live advice. What remains live is the sequencing:
> the execution seam, the isolation gates, and Iceberg interoperability are still unbuilt and still
> gate the claim.

**Adopt the engineering discipline, not the label.** Everything an enterprise claim would require
that is worth building — the execution seam, isolation containment, interoperability — is worth
building for the current position too. The label additionally requires compliance, support, and a
commercial entity, none of which are engineering and none of which exist.

**Build the execution seam before claiming pluggability.** An `IQueryExecutor`-shaped boundary makes
the single-node ceiling a bounded engineering problem instead of an identity, and it is cheap
relative to its strategic value. Today the claim would be unsupported by any code.

**Consider a segment reframe rather than a category move.** Something like "sovereign lakehouse for
regulated .NET estates" retains the defensible wedge, targets buyers the incumbents structurally
cannot serve, and carries the seriousness an enterprise framing reaches for — without inviting
scoring on rows that return "not built."

**Sequence against counted demand.** In order: partition management (largest demand signal, existing
surface, smallest delta), the Power BI type shim (unblocks the core segment's default BI tool),
Iceberg REST (the moat and the interoperability answer simultaneously), then the semantic layer from
the EF Core model (the agentic bet, uniquely available here).

**Close roadmap Phases 1–3 before any enterprise claim is made externally.** Untrusted-tenant SQL
containment in particular. The roadmap already requires this; a category claim would require it
sooner and more publicly.

## What this review did not assess

Recorded so the gaps are not mistaken for findings:

- **No performance or scale measurement.** The single-node ceiling is described architecturally; no
  benchmark was run. [`PROVIDER-FEEDBACK.md`](PROVIDER-FEEDBACK.md) holds the existing measurements.
- **No security review.** The authorization *design* was read; no adversarial testing was performed.
  The Phase 3 containment gap is quoted from the roadmap, not independently exercised.
- **No frontend assessment** beyond noting which surfaces exist. [`UI.md`](UI.md) is the record.
- **No pricing, packaging, or licensing analysis**, and no legal review of what an enterprise
  support obligation would entail under Apache-2.0 plus the CLA.
- **No customer evidence.** The demand section reasons from public trackers and standard evaluation
  checklists. A single lost deal would outweigh most of it.
