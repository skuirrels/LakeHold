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
  /**
   * The schema.org type of the *document* this page is. Omitted on the home page, which does not
   * describe the product from a distance — it is the product's own page, and so carries the
   * `SoftwareApplication`, `WebSite`, and `Organization` entities the whole site hangs off.
   *
   * Every other indexable page declares one, which is what keeps a search for "lakehold" pointed at
   * the home page: a documentation page that announces itself as an article *about* an application
   * described elsewhere is a weaker answer to the product's name than the page it points at.
   */
  readonly documentType?: 'TechArticle' | 'WebPage';
  /** Trailing breadcrumb label, under the site root. Required wherever `documentType` is set. */
  readonly breadcrumb?: string;
}

/** Stable JSON-LD node identifiers, so an article can reference the entities without repeating them. */
const WEBSITE_ID = `${SITE_ORIGIN}/#website`;
const ORGANIZATION_ID = `${SITE_ORIGIN}/#organization`;
const SOFTWARE_ID = `${SITE_ORIGIN}/#software`;

/** The `id` of the single JSON-LD script tag this service owns, so navigation replaces rather than appends. */
const JSON_LD_ELEMENT_ID = 'lh-structured-data';

/**
 * Applies per-route `<meta>` tags, the canonical link, and the JSON-LD graph on every navigation.
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
    this.setTag('property', 'og:type', data?.documentType === undefined ? 'website' : 'article');
    this.setTag('property', 'og:site_name', 'LakeHold');
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
      this.setStructuredData(null);
    } else {
      this.meta.removeTag("name='robots'");
      this.setCanonical(url);
      this.setStructuredData(buildStructuredData(data, url, title, description));
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

  /**
   * Writes this page's JSON-LD graph, replacing whatever the previous route left behind. Passing
   * `null` removes it: a page kept out of the index should not describe an entity either.
   */
  private setStructuredData(graph: object | null): void {
    const existing = this.document.getElementById(JSON_LD_ELEMENT_ID) ?? null;
    if (graph === null) {
      existing?.remove();
      return;
    }

    const script = existing ?? this.document.createElement('script');
    if (existing === null) {
      script.setAttribute('type', 'application/ld+json');
      script.setAttribute('id', JSON_LD_ELEMENT_ID);
      this.document.head.appendChild(script);
    }

    // The graph is built from route data compiled into the bundle, never from a URL or user input,
    // so there is nothing here that could close the script element early.
    script.textContent = JSON.stringify(graph);
  }
}

/**
 * Builds the page's JSON-LD.
 *
 * Both branches emit the same `@id`s, so the `WebSite`, `Organization`, and `SoftwareApplication`
 * nodes an article references are the ones the home page defines rather than near-copies. That is
 * the whole point of the split: one page is the product, the rest are documents about it.
 */
function buildStructuredData(
  data: SeoData | undefined,
  url: string,
  title: string,
  description: string,
): object {
  const website = {
    '@type': 'WebSite',
    '@id': WEBSITE_ID,
    url: `${SITE_ORIGIN}/`,
    name: 'LakeHold',
    publisher: { '@id': ORGANIZATION_ID },
  };

  const organization = {
    '@type': 'Organization',
    '@id': ORGANIZATION_ID,
    name: 'LakeHold',
    url: `${SITE_ORIGIN}/`,
    logo: `${SITE_ORIGIN}/icons/icon-512.png`,
    sameAs: ['https://github.com/skuirrels/LakeHold'],
  };

  if (data?.documentType === undefined) {
    return {
      '@context': 'https://schema.org',
      '@graph': [
        website,
        organization,
        {
          '@type': 'SoftwareApplication',
          '@id': SOFTWARE_ID,
          name: 'LakeHold',
          alternateName: 'LakeHold lakehouse',
          url: `${SITE_ORIGIN}/`,
          mainEntityOfPage: `${SITE_ORIGIN}/`,
          applicationCategory: 'DeveloperApplication',
          applicationSubCategory: 'Database',
          operatingSystem: 'Linux, macOS, Windows',
          license: 'https://www.apache.org/licenses/LICENSE-2.0',
          description:
            'A feature-rich DuckDB and DuckLake lakehouse you host yourself: tenant-aware catalogs, time travel, change data capture, a versioned API with first-party Java, .NET, and Go source SDKs, and every byte stored as open Parquet.',
          offers: { '@type': 'Offer', price: '0', priceCurrency: 'USD' },
          codeRepository: 'https://github.com/skuirrels/LakeHold',
          sameAs: ['https://github.com/skuirrels/LakeHold'],
          publisher: { '@id': ORGANIZATION_ID },
        },
      ],
    };
  }

  return {
    '@context': 'https://schema.org',
    '@graph': [
      website,
      organization,
      {
        '@type': data.documentType,
        '@id': `${url}#document`,
        url,
        name: title,
        headline: title,
        description,
        inLanguage: 'en',
        isPartOf: { '@id': WEBSITE_ID },
        // Named rather than a bare `@id` reference: the node itself is only defined in full on the
        // home page, and a reference a parser cannot resolve is one it is entitled to drop. This
        // says what the page is about *and* where that subject's own page is.
        about: {
          '@type': 'SoftwareApplication',
          '@id': SOFTWARE_ID,
          name: 'LakeHold',
          url: `${SITE_ORIGIN}/`,
        },
        publisher: { '@id': ORGANIZATION_ID },
      },
      {
        '@type': 'BreadcrumbList',
        '@id': `${url}#breadcrumbs`,
        itemListElement: [
          { '@type': 'ListItem', position: 1, name: 'LakeHold', item: `${SITE_ORIGIN}/` },
          { '@type': 'ListItem', position: 2, name: data.breadcrumb ?? title, item: url },
        ],
      },
    ],
  };
}

function deepestChild(route: ActivatedRoute): ActivatedRoute {
  let current = route;
  while (current.firstChild !== null) {
    current = current.firstChild;
  }

  return current;
}
