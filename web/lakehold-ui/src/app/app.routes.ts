import { Routes } from '@angular/router';

/**
 * Marketing pages are lazily loaded and the workbench is not: whichever surface you land on, the
 * other one is dead weight in the initial bundle, and the comparison page in particular is only
 * read once.
 *
 * `data.seo` carries the description, indexing rules, and JSON-LD shape each route needs;
 * `SeoService` applies them on navigation and the prerenderer bakes them into the emitted HTML.
 * Keep a description under about 160 characters — a search result snippet is truncated past
 * roughly that.
 *
 * Every indexable route except the home page declares a `documentType` and a `breadcrumb`. That is
 * what makes the home page the site's answer to a search for "lakehold": those pages publish
 * themselves as articles *about* the product, and name the home page as where the product itself
 * lives. Omitting them on a new page silently enters it into that competition.
 */
export const routes: Routes = [
  {
    path: '',
    title: 'LakeHold — a feature-rich DuckDB lakehouse you host yourself',
    data: {
      seo: {
        description:
          'A DuckDB and DuckLake lakehouse you host yourself: time travel, change data capture, a PostgreSQL wire endpoint, and every byte stored as open Parquet.',
      },
    },
    loadComponent: () => import('./landing.component').then((m) => m.LandingComponent),
  },
  {
    path: 'enterprise-data-platform',
    title: 'Enterprise Data Platform — LakeHold',
    data: {
      seo: {
        description:
          'How LakeHold is becoming a private Enterprise Data Platform: governed ingestion, open lakehouse storage, consumption, operations, and an honest capability roadmap.',
      },
    },
    loadComponent: () =>
      import('./enterprise-data-platform.component').then((m) => m.EnterpriseDataPlatformComponent),
  },
  {
    path: 'compare',
    title: 'LakeHold vs MotherDuck, ClickHouse, and the cloud warehouses',
    data: {
      seo: {
        description:
          'How LakeHold compares with MotherDuck, ClickHouse, Snowflake, and Databricks on data ownership, open storage, self-hosting, and cost — including where it loses.',
        documentType: 'WebPage',
        breadcrumb: 'Comparison',
      },
    },
    loadComponent: () => import('./comparison.component').then((m) => m.ComparisonComponent),
  },
  {
    path: 'docs',
    title: 'Documentation — get started with LakeHold',
    data: {
      seo: {
        description:
          'Run LakeHold with Docker Compose, query a catalog from the workbench, travel through snapshots, and use eject, backup, CDC, and the PostgreSQL wire endpoint.',
        documentType: 'TechArticle',
        breadcrumb: 'Documentation',
      },
    },
    loadComponent: () => import('./docs.component').then((m) => m.DocsComponent),
  },
  {
    path: 'docs/linq-workbench',
    title: 'C# LINQ Workbench — LakeHold documentation',
    data: {
      seo: {
        description:
          'Write C# LINQ in the LakeHold Workbench with an isolated DuckDB.EFCoreProvider planner, generated SQL, diagnostics, saved queries, and safe deployment.',
      },
    },
    loadComponent: () =>
      import('./linq-workbench-docs.component').then((m) => m.LinqWorkbenchDocsComponent),
  },
  {
    path: 'docs/connectors',
    title: 'Managed data connectors — LakeHold documentation',
    data: {
      seo: {
        description:
          'Configure LakeHold REST/gRPC snapshots and PostgreSQL/HubSpot incremental connectors with checkpoints, retries, schema contracts, secrets, and lineage.',
      },
    },
    loadComponent: () =>
      import('./managed-connectors-docs.component').then((m) => m.ManagedConnectorsDocsComponent),
  },
  {
    path: 'docs/enterprise-data-platform-roadmap',
    title: 'Enterprise Data Platform delivery plan — LakeHold',
    data: {
      seo: {
        description:
          'LakeHold Enterprise Data Platform delivery status: what is implemented, partial, unreleased, not started, and required for Priority 1 completion.',
      },
    },
    loadComponent: () =>
      import('./enterprise-data-platform-roadmap.component').then(
        (m) => m.EnterpriseDataPlatformRoadmapComponent,
      ),
  },
  {
    path: 'docs/operations',
    title: 'Operations — run LakeHold in production',
    data: {
      seo: {
        description:
          'Operate LakeHold in production with ownership, readiness gates, routine checks, safe deployment, rollback, and evidence-handling guidance.',
      },
    },
    loadComponent: () => import('./operations.component').then((m) => m.OperationsComponent),
  },
  {
    path: 'docs/incident-response',
    title: 'Incident response — LakeHold operations',
    data: {
      seo: {
        description:
          'Respond to LakeHold incidents with severity levels, first-response checks, diagnosis, containment, recovery, communication, and review procedures.',
      },
    },
    loadComponent: () =>
      import('./incident-response.component').then((m) => m.IncidentResponseComponent),
  },
  {
    path: 'docs/disaster-recovery',
    title: 'Disaster recovery — LakeHold operations',
    data: {
      seo: {
        description:
          'Recover LakeHold state with explicit protection boundaries, RPO and RTO guidance, full-node and catalog procedures, validation, and restore drills.',
      },
    },
    loadComponent: () =>
      import('./disaster-recovery.component').then((m) => m.DisasterRecoveryComponent),
  },
  {
    path: 'docs/monitoring',
    title: 'Monitoring and alerting — LakeHold operations',
    data: {
      seo: {
        description:
          'Monitor LakeHold health, OpenTelemetry signals, maintenance, backups, capacity, security, and CDC with actionable alerts and validation drills.',
      },
    },
    loadComponent: () => import('./monitoring.component').then((m) => m.MonitoringComponent),
  },
  {
    path: 'provider',
    title: 'DuckDB.EFCoreProvider — DuckDB, DuckLake and Parquet for EF Core 10',
    data: {
      seo: {
        description:
          'The EF Core 10 provider LakeHold runs on: LINQ, writes, non-executing command plans, DuckLake time travel, and Parquet tiers on S3, GCS, or Azure.',
        documentType: 'WebPage',
        breadcrumb: 'DuckDB.EFCoreProvider',
      },
    },
    loadComponent: () => import('./provider.component').then((m) => m.ProviderComponent),
  },
  {
    path: 'provider/docs',
    title: 'DuckDB.EFCoreProvider documentation — EF Core 10 on DuckDB',
    data: {
      seo: {
        description:
          'DuckDB.EFCoreProvider reference: configure EF Core, translate LINQ, capture command plans, query Parquet, and run DuckLake and tiered storage.',
        documentType: 'TechArticle',
        breadcrumb: 'DuckDB.EFCoreProvider documentation',
      },
    },
    loadComponent: () => import('./provider-docs.component').then((m) => m.ProviderDocsComponent),
  },
  {
    path: 'workbench',
    title: 'Workbench — LakeHold',
    // Behind authentication and meaningless without a running instance, so it stays out of the index.
    data: {
      seo: {
        description: '',
        noIndex: true,
      },
    },
    loadComponent: () => import('./workbench.component').then((m) => m.WorkbenchComponent),
  },
  { path: '**', redirectTo: '' },
];
