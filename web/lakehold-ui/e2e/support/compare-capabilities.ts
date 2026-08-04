export type CompareTone = 'good' | 'weak' | 'neutral';

export type EvidenceLane =
  | 'phase2'
  | 'browser'
  | 'backend'
  | 'storage-integration'
  | 'deployment-contract'
  | 'declared-boundary'
  | 'roadmap';

export interface CompareEvidence {
  lane: EvidenceLane;
  path: string;
  marker: string;
  proves: string;
}

export interface CompareCapability {
  dimension: string;
  claim: string;
  tone: CompareTone;
  evidence: readonly CompareEvidence[];
}

/**
 * Executable traceability contract for every LakeHold cell in the /compare matrix.
 *
 * This intentionally duplicates the reader-facing text. If marketing copy changes, the browser
 * test fails until the new claim is assigned concrete evidence. A declared limitation or roadmap
 * item is evidence too: the suite must preserve the honest boundary rather than pretending an
 * unavailable feature was simulated.
 */
export const compareCapabilities: readonly CompareCapability[] = [
  {
    dimension: 'Deployment',
    claim: 'Self-hosted anywhere, incl. air-gapped',
    tone: 'good',
    evidence: [
      {
        lane: 'deployment-contract',
        path: 'scripts/test-phase2.sh',
        marker: 'up --detach --build --wait api workbench webhook',
        proves: 'A production-shaped node is built and operated entirely from local containers.',
      },
    ],
  },
  {
    dimension: 'Where your data lives',
    claim: 'Your disk or object store, under your control',
    tone: 'good',
    evidence: [
      {
        lane: 'phase2',
        path: 'web/lakehold-ui/e2e/phase2-operator.spec.ts',
        marker: 'independently verify the final API and wire-protocol state',
        proves: 'The disposable node owns and verifies its local catalog and data state.',
      },
      {
        lane: 'storage-integration',
        path: 'tests/Lakehold.Engine.Tests/StorageBrowserIntegrationTests.cs',
        marker: 'The_rollup_is_correct_when_the_data_lives_in_a_bucket',
        proves: 'The same storage surface reads a real S3-compatible object-store catalog.',
      },
    ],
  },
  {
    dimension: 'Accounts, SSO, permissions',
    claim:
      'OIDC browser sign-in, scoped API tokens, three roles, in-product member and token administration; no row policies',
    tone: 'neutral',
    evidence: [
      {
        lane: 'phase2',
        path: 'web/lakehold-ui/e2e/phase2-operator.spec.ts',
        marker: 'exercise all three roles, revocation, expiry, and browser recovery',
        proves:
          'Owner, editor, and reader credentials cross real HTTP, browser, and wire boundaries.',
      },
      {
        lane: 'backend',
        path: 'tests/Lakehold.Api.Tests/MemberDirectoryTests.cs',
        marker: 'A_demotion_in_Lakehold_survives_the_provider_reasserting_the_old_role',
        proves:
          'Authorization is owned by the membership record, not by whatever role the provider asserts.',
      },
      {
        lane: 'browser',
        path: 'web/lakehold-ui/e2e/identity.spec.ts',
        marker: 'lets an administrator change what they reach',
        proves:
          'An administrator signs in and administers a person end to end, in the product itself.',
      },
      {
        lane: 'declared-boundary',
        path: 'web/lakehold-ui/src/app/comparison.component.ts',
        marker: 'member and token administration; no row policies',
        proves:
          'The shipped member and credential administration and the remaining row-policy limitation are explicit.',
      },
    ],
  },
  {
    dimension: 'Table format',
    claim: 'DuckLake — plain Parquet + SQL catalog',
    tone: 'good',
    evidence: [
      {
        lane: 'backend',
        path: 'tests/Lakehold.Engine.Tests/CatalogEjectTests.cs',
        marker: 'Eject_writes_reader_agnostic_parquet_with_deletions_and_updates_applied',
        proves: 'DuckLake data is materialised as ordinary Parquet and read independently.',
      },
      {
        lane: 'storage-integration',
        path: 'tests/Lakehold.Engine.Tests/PostgresCatalogBackupTests.cs',
        marker: 'Postgres_metadata_backs_up_and_restores_into_a_local_catalog',
        proves: 'The SQL metadata catalog is exercised against PostgreSQL as well as a local file.',
      },
    ],
  },
  {
    dimension: 'Read data without the product',
    claim: 'Yes — tested, see exit path',
    tone: 'good',
    evidence: [
      {
        lane: 'backend',
        path: 'tests/Lakehold.Engine.Tests/CatalogEjectTests.cs',
        marker: 'Eject_manifest_row_counts_match_an_independent_reader',
        proves: 'A plain DuckDB Parquet reader verifies every exported table without LakeHold.',
      },
    ],
  },
  {
    dimension: 'Other engines read it live',
    claim: 'Eject or export today; Iceberg REST planned',
    tone: 'weak',
    evidence: [
      {
        lane: 'phase2',
        path: 'web/lakehold-ui/e2e/phase2-operator.spec.ts',
        marker: 'apply destructive maintenance only on disposable state and verify signed export',
        proves: 'The available batch exit path is exercised as a real operator.',
      },
      {
        lane: 'roadmap',
        path: 'web/lakehold-ui/src/app/comparison.component.ts',
        marker: 'Iceberg REST planned',
        proves: 'Live cross-engine reads remain labelled as planned rather than shipped.',
      },
    ],
  },
  {
    dimension: 'Time travel',
    claim: 'Yes — query your data from an earlier point in time',
    tone: 'good',
    evidence: [
      {
        lane: 'phase2',
        path: 'web/lakehold-ui/e2e/phase2-operator.spec.ts',
        marker: 'drill into data history, compare, restore, and verify the live data',
        proves:
          'Historical rows are browsed, a bounded change range is compared, and the selected snapshot restores live rows.',
      },
      {
        lane: 'storage-integration',
        path: 'tests/Lakehold.Engine.Tests/CatalogBackupRoundTripTests.cs',
        marker: 'Restore_reproduces_contents_deletions_and_history',
        proves: 'Snapshot history survives a complete metadata backup and restore.',
      },
    ],
  },
  {
    dimension: 'Verified, signed export',
    claim: 'One call — row-count attested and signed',
    tone: 'good',
    evidence: [
      {
        lane: 'phase2',
        path: 'web/lakehold-ui/e2e/phase2-operator.spec.ts',
        marker: 'verify signed export',
        proves: 'The user-facing eject reports both verification and signing on a live node.',
      },
      {
        lane: 'backend',
        path: 'tests/Lakehold.Engine.Tests/CatalogEjectTests.cs',
        marker: 'Eject_signs_the_manifest_and_the_signature_detects_tampering',
        proves:
          'An independent verifier accepts the manifest and rejects tampering or a wrong key.',
      },
    ],
  },
  {
    dimension: 'Change data capture',
    claim: 'Built in — typed feed + signed webhooks',
    tone: 'good',
    evidence: [
      {
        lane: 'phase2',
        path: 'web/lakehold-ui/e2e/phase2-operator.spec.ts',
        marker:
          'read the typed feed and verify signed webhook failure, retry, and cursor advancement',
        proves: 'The browser reads typed changes and a receiver verifies stable signed retries.',
      },
      {
        lane: 'backend',
        path: 'tests/Lakehold.Engine.Tests/ChangeFeedTests.cs',
        marker: 'Change_feed_reports_inserts_deletes_and_updates_with_values',
        proves: 'Insert, delete, and update images retain their typed row values.',
      },
    ],
  },
  {
    dimension: 'Managed ingestion',
    claim:
      'REST/gRPC plus PostgreSQL/HubSpot incremental adapters shipped in v1.3.0; broad catalogue pending',
    tone: 'neutral',
    evidence: [
      {
        lane: 'backend',
        path: 'tests/Lakehold.Api.Tests/DataConnectorTransportIntegrationTests.cs',
        marker: 'PostgreSql_adapter_reads_only_rows_after_the_typed_checkpoint',
        proves:
          'A real PostgreSQL source verifies typed incremental cursor reads against the repository test database.',
      },
      {
        lane: 'backend',
        path: 'tests/Lakehold.Api.Tests/DataConnectorTransportIntegrationTests.cs',
        marker: 'HubSpot_adapter_renews_oauth_and_returns_incremental_checkpoint',
        proves: 'The HubSpot adapter renews OAuth credentials and proposes a durable checkpoint.',
      },
      {
        lane: 'declared-boundary',
        path: 'docs/CONNECTORS.md',
        marker: 'not a Fivetran-sized connector',
        proves: 'The built-in catalogue remains explicitly limited to four adapters.',
      },
    ],
  },
  {
    dimension: 'AI / MCP',
    claim: 'Authenticated MCP; read tools + operator-gated writes',
    tone: 'good',
    evidence: [
      {
        lane: 'phase2',
        path: 'web/lakehold-ui/e2e/phase2-operator.spec.ts',
        marker: 'use authenticated MCP reads and operator-gated writes as external clients',
        proves: 'External owner, editor, and reader clients exercise both MCP write gates.',
      },
      {
        lane: 'backend',
        path: 'tests/Lakehold.Api.Tests/McpWriteToolTests.cs',
        marker: 'The_write_tool_is_absent_unless_the_operator_enables_it',
        proves: 'The execute tool is absent until the deployment explicitly opts in.',
      },
    ],
  },
  {
    dimension: 'BI tools (Power BI, Tableau)',
    claim: 'Postgres wire protocol; Power BI blocked on type loading',
    tone: 'weak',
    evidence: [
      {
        lane: 'phase2',
        path: 'web/lakehold-ui/e2e/phase2-operator.spec.ts',
        marker: 'including revocation through a real psql connection',
        proves:
          'A real PostgreSQL client queries with a tenant token and loses access on revocation.',
      },
      {
        lane: 'backend',
        path: 'tests/Lakehold.Api.Tests/PgWireEndpointTests.cs',
        marker: 'Column_types_are_declared_so_the_client_reads_them_natively',
        proves: 'The wire implementation is exercised through Npgsql with native result types.',
      },
      {
        lane: 'declared-boundary',
        path: 'web/lakehold-ui/src/app/comparison.component.ts',
        marker: 'Power BI blocked on type loading',
        proves: 'The unsupported Power BI path remains visibly blocked, not claimed as passing.',
      },
    ],
  },
  {
    dimension: 'Maintenance control',
    claim: 'Explicit, dry-run by default',
    tone: 'neutral',
    evidence: [
      {
        lane: 'phase2',
        path: 'web/lakehold-ui/e2e/phase2-operator.spec.ts',
        marker: 'Dry run — nothing was changed.',
        proves: 'Destructive controls show a no-change preview before an explicit apply.',
      },
    ],
  },
  {
    dimension: '.NET / EF Core',
    claim: 'One model for app and lake; client package pending',
    tone: 'good',
    evidence: [
      {
        lane: 'backend',
        path: 'tests/Lakehold.Api.Tests/EfInstrumentationTests.cs',
        marker: 'The_instrumentation_is_wired_and_produces_a_span_for_a_statement',
        proves: 'The live data path executes through the EF Core-backed LakeContext.',
      },
      {
        lane: 'declared-boundary',
        path: 'web/lakehold-ui/src/app/comparison.component.ts',
        marker: 'client package pending',
        proves: 'The not-yet-published client package remains explicitly qualified.',
      },
    ],
  },
  {
    dimension: 'Scale ceiling',
    claim: 'Scale out workers; each query stays on one node',
    tone: 'weak',
    evidence: [
      {
        lane: 'declared-boundary',
        path: 'docs/ARCHITECTURE.md',
        marker: 'single query remains node-bound',
        proves: 'Workers scale independently while each query remains node-bound.',
      },
    ],
  },
  {
    dimension: 'Concurrent writers',
    claim: 'PostgreSQL-backed DuckLake metadata; worker-local execution',
    tone: 'neutral',
    evidence: [
      {
        lane: 'architecture',
        path: 'docs/ARCHITECTURE.md',
        marker: 'PostgreSQL holds the shared control plane and each new DuckLake metadata schema',
        proves: 'Catalog metadata is shared in PostgreSQL while execution remains worker-local.',
      },
    ],
  },
  {
    dimension: 'Operational burden',
    claim: 'You run it',
    tone: 'weak',
    evidence: [
      {
        lane: 'deployment-contract',
        path: 'scripts/test-phase2.sh',
        marker: 'docker compose -p "$compose_project"',
        proves: 'The lifecycle test makes the operator start, verify, and remove the node.',
      },
    ],
  },
  {
    dimension: 'Licence',
    claim: 'Apache-2.0',
    tone: 'good',
    evidence: [
      {
        lane: 'deployment-contract',
        path: 'LICENSE',
        marker: 'Apache License',
        proves: 'The repository carries the Apache 2.0 licence text.',
      },
    ],
  },
  {
    dimension: 'Cost shape',
    claim: 'A VM and a bucket',
    tone: 'neutral',
    evidence: [
      {
        lane: 'deployment-contract',
        path: 'compose.production.yaml',
        marker: 'services:',
        proves:
          'The shipped self-hosted topology is an operator-owned service stack, not usage billing.',
      },
    ],
  },
] as const;
