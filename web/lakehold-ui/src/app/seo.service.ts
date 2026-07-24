import { Injectable, inject } from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { Meta } from '@angular/platform-browser';
import { ActivatedRoute, NavigationEnd, Router } from '@angular/router';
import { filter, map } from 'rxjs/operators';

/** The production origin. Canonical, Open Graph, and sitemap URLs must all be absolute and agree. */
export const SITE_ORIGIN = 'https://lakehold.dev';

/** Per-route metadata, declared beside the route's `title` in `app.routes.ts`. */
export interface SeoData {
  /** The `<meta name="description">`, and the Open Graph description. Aim for 150–160 characters. */
  readonly description: string;
  /** Kept out of the index — set on the workbench, which is behind authentication. */
  readonly noIndex?: boolean;
}

/**
 * Applies per-route `<meta>` tags, and the canonical link, on every navigation.
 *
 * Titles stay with the router's own `title` resolver; everything a crawler or a link unfurler reads
 * beyond the title is centralised here so the marketing copy lives in one reviewable place rather
 * than being spread across the page components.
 *
 * This runs during prerendering too, so the emitted HTML carries the tags without JavaScript.
 */
@Injectable({ providedIn: 'root' })
export class SeoService {
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly meta = inject(Meta);
  private readonly document = inject(DOCUMENT);

  /** Subscribes to navigation. Called once, from the app shell. */
  init(): void {
    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        map(() => deepestChild(this.route).snapshot),
      )
      .subscribe((snapshot) => {
        const data = snapshot.data['seo'] as SeoData | undefined;
        const url = `${SITE_ORIGIN}${this.router.url.split(/[?#]/)[0]}`;
        this.apply(data, url, snapshot.title ?? '');
      });
  }

  private apply(data: SeoData | undefined, url: string, title: string): void {
    // A route with no SEO data is one that should not be advertised; leaving stale tags from the
    // previous page in place would be worse than describing nothing.
    const description = data?.description ?? '';

    this.setTag('name', 'description', description);
    this.setTag('property', 'og:title', title);
    this.setTag('property', 'og:description', description);
    this.setTag('property', 'og:url', url);
    this.setTag('property', 'og:type', 'website');
    this.setTag('property', 'og:site_name', 'Lakehold');
    this.setTag('property', 'og:image', `${SITE_ORIGIN}/icons/og-image.png`);
    this.setTag('name', 'twitter:card', 'summary_large_image');
    this.setTag('name', 'twitter:title', title);
    this.setTag('name', 'twitter:description', description);
    this.setTag('name', 'twitter:image', `${SITE_ORIGIN}/icons/og-image.png`);

    // A page kept out of the index must not also nominate itself as a canonical URL: the two are
    // contradictory signals, and only the indexable pages have a canonical worth stating.
    if (data?.noIndex === true) {
      this.meta.updateTag({ name: 'robots', content: 'noindex, nofollow' });
      this.removeCanonical();
    } else {
      this.meta.removeTag("name='robots'");
      this.setCanonical(url);
    }
  }

  private setTag(attribute: 'name' | 'property', key: string, content: string): void {
    if (content.length === 0) {
      this.meta.removeTag(`${attribute}='${key}'`);
      return;
    }

    this.meta.updateTag({ [attribute]: key, content }, `${attribute}='${key}'`);
  }

  /**
   * Points the canonical link at the current route. Without one, a page reachable at more than one
   * URL — a trailing slash, a tracking parameter — can be indexed as several competing duplicates.
   */
  private setCanonical(url: string): void {
    // The server-side DOM used while prerendering returns `undefined` rather than `null` for a
    // miss, so this has to be a nullish check and not a strict comparison against null.
    let link = this.document.head.querySelector<HTMLLinkElement>("link[rel='canonical']") ?? null;
    if (link === null) {
      link = this.document.createElement('link');
      link.setAttribute('rel', 'canonical');
      this.document.head.appendChild(link);
    }

    link.setAttribute('href', url);
  }

  private removeCanonical(): void {
    this.document.head.querySelector("link[rel='canonical']")?.remove();
  }
}

function deepestChild(route: ActivatedRoute): ActivatedRoute {
  let current = route;
  while (current.firstChild !== null) {
    current = current.firstChild;
  }

  return current;
}
