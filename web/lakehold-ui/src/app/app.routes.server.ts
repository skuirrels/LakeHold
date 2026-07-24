import { RenderMode, ServerRoute } from '@angular/ssr';

/**
 * Render-mode map for the static build.
 *
 * The marketing surfaces (`/`, `/compare`, `/docs`) are prerendered to real HTML at build time so
 * search crawlers and link unfurlers receive content instead of an empty `<app-root>` shell. The
 * workbench sits behind authentication and has nothing to index, so it stays client-rendered.
 */
export const serverRoutes: ServerRoute[] = [
  { path: 'workbench', renderMode: RenderMode.Client },
  { path: '**', renderMode: RenderMode.Prerender },
];
