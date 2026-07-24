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
    title: 'Lakehold — your lakehouse, held outright',
    data: {
      seo: {
        description:
          'A self-hostable DuckDB and DuckLake lakehouse that runs on your own infrastructure, stores every byte as open Parquet you can read without us, and speaks .NET natively.',
      },
    },
    loadComponent: () => import('./landing.component').then((m) => m.LandingComponent),
  },
  {
    path: 'compare',
    title: 'Lakehold vs MotherDuck, ClickHouse, and the cloud warehouses',
    data: {
      seo: {
        description:
          'How Lakehold compares with MotherDuck, ClickHouse, Snowflake, and Databricks on data ownership, open storage, self-hosting, and cost — including where it loses.',
      },
    },
    loadComponent: () => import('./comparison.component').then((m) => m.ComparisonComponent),
  },
  {
    path: 'docs',
    title: 'Documentation — get started with Lakehold',
    data: {
      seo: {
        description:
          'Run Lakehold with Docker Compose, query a catalog from the workbench, travel through snapshots, and use eject, backup, CDC, and the PostgreSQL wire endpoint.',
      },
    },
    loadComponent: () => import('./docs.component').then((m) => m.DocsComponent),
  },
  {
    path: 'workbench',
    title: 'Workbench — Lakehold',
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
