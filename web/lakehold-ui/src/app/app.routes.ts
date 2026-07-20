import { Routes } from '@angular/router';

/**
 * Marketing pages are lazily loaded and the workbench is not: whichever surface you land on, the
 * other one is dead weight in the initial bundle, and the comparison page in particular is only
 * read once.
 */
export const routes: Routes = [
  {
    path: '',
    title: 'Lakehold — your lakehouse, held outright',
    loadComponent: () => import('./landing.component').then((m) => m.LandingComponent),
  },
  {
    path: 'compare',
    title: 'Lakehold vs MotherDuck, ClickHouse, and the cloud warehouses',
    loadComponent: () => import('./comparison.component').then((m) => m.ComparisonComponent),
  },
  {
    path: 'workbench',
    title: 'Workbench — Lakehold',
    loadComponent: () => import('./workbench.component').then((m) => m.WorkbenchComponent),
  },
  { path: '**', redirectTo: '' },
];
