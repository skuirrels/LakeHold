import { Routes } from '@angular/router';

/**
 * Marketing pages are lazily loaded and the workbench is not: whichever surface you land on, the
 * other one is dead weight in the initial bundle, and the comparison page in particular is only
 * read once.
 *
 * `data.seo` carries the description and indexing rules each route needs; `SeoService` applies them
 * on navigation and the prerenderer bakes them into the emitted HTML. Keep a description under
 * about 160 characters — a search result snippet is truncated past roughly that.
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
    path: 'compare',
    title: 'LakeHold vs MotherDuck, ClickHouse, and the cloud warehouses',
    data: {
      seo: {
        description:
          'How LakeHold compares with MotherDuck, ClickHouse, Snowflake, and Databricks on data ownership, open storage, self-hosting, and cost — including where it loses.',
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
      },
    },
    loadComponent: () => import('./docs.component').then((m) => m.DocsComponent),
  },
  {
    path: 'provider',
    title: 'DuckDB.EFCoreProvider — DuckDB, DuckLake and Parquet for EF Core 10',
    data: {
      seo: {
        description:
          'The EF Core 10 provider LakeHold runs on: native LINQ and writes on DuckDB, DuckLake catalogs with time travel, and hot-to-cold Parquet tiers on S3, GCS, or Azure.',
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
          'Reference documentation for DuckDB.EFCoreProvider: install and configure it, translate LINQ, pick a write path, query Parquet, and run DuckLake and tiered storage.',
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
