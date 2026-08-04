import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Route, Router, provideRouter } from '@angular/router';
import { routes } from './app.routes';
import { SITE_ORIGIN, SeoData, SeoService } from './seo.service';

@Component({ template: '' })
class BlankComponent {}

/**
 * The tags are only worth testing where they carry a decision. Descriptions and titles are copy;
 * what breaks silently — and only shows up weeks later in a search result — is the structured data
 * telling a crawler that a documentation page describes the product as directly as the home page.
 */
describe('SeoService', () => {
  function jsonLd(): Record<string, unknown> | null {
    const script = document.getElementById('lh-structured-data');
    return script === null ? null : (JSON.parse(script.textContent ?? '{}') as Record<string, unknown>);
  }

  function graph(): { '@type': string; [key: string]: unknown }[] {
    return (jsonLd()?.['@graph'] ?? []) as { '@type': string; [key: string]: unknown }[];
  }

  function node(type: string): { [key: string]: unknown } | undefined {
    return graph().find((entry) => entry['@type'] === type);
  }

  beforeEach(async () => {
    document.getElementById('lh-structured-data')?.remove();
    document.head.querySelector("link[rel='canonical']")?.remove();

    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          {
            path: '',
            component: BlankComponent,
            title: 'LakeHold — a feature-rich DuckDB lakehouse you host yourself',
            data: { seo: { description: 'The home page.' } },
          },
          {
            path: 'docs',
            component: BlankComponent,
            title: 'Documentation — get started with LakeHold',
            data: {
              seo: {
                description: 'The documentation.',
                documentType: 'TechArticle',
                breadcrumb: 'Documentation',
              },
            },
          },
          {
            path: 'workbench',
            component: BlankComponent,
            title: 'Workbench — LakeHold',
            data: { seo: { description: '', noIndex: true } },
          },
        ]),
      ],
    });

    TestBed.inject(SeoService).init();
    await TestBed.inject(Router).navigateByUrl('/');
  });

  it('makes the home page the product itself', () => {
    expect(node('SoftwareApplication')?.['url']).toBe(`${SITE_ORIGIN}/`);
    expect(node('WebSite')?.['name']).toBe('LakeHold');
    expect(node('Organization')?.['name']).toBe('LakeHold');
    // An article on the home page would put it in competition with its own documentation.
    expect(node('TechArticle')).toBeUndefined();
    expect(node('BreadcrumbList')).toBeUndefined();
  });

  it('makes a documentation page an article that points back at the home page', async () => {
    await TestBed.inject(Router).navigateByUrl('/docs');

    const article = node('TechArticle');
    expect(article?.['url']).toBe(`${SITE_ORIGIN}/docs`);
    expect(article?.['isPartOf']).toEqual({ '@id': `${SITE_ORIGIN}/#website` });
    expect(article?.['about']).toEqual(
      expect.objectContaining({ '@type': 'SoftwareApplication', url: `${SITE_ORIGIN}/` }),
    );

    // The full product entity is the home page's alone; repeating it here is the regression.
    expect(node('SoftwareApplication')).toBeUndefined();

    expect(node('BreadcrumbList')?.['itemListElement']).toEqual([
      { '@type': 'ListItem', position: 1, name: 'LakeHold', item: `${SITE_ORIGIN}/` },
      { '@type': 'ListItem', position: 2, name: 'Documentation', item: `${SITE_ORIGIN}/docs` },
    ]);
  });

  it('leaves only one script tag behind across navigations', async () => {
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/docs');
    await router.navigateByUrl('/');

    expect(document.querySelectorAll('script#lh-structured-data')).toHaveLength(1);
    expect(node('SoftwareApplication')).toBeDefined();
    expect(node('TechArticle')).toBeUndefined();
  });

  it('describes no entity on a page kept out of the index', async () => {
    await TestBed.inject(Router).navigateByUrl('/workbench');

    expect(jsonLd()).toBeNull();
    expect(document.head.querySelector("link[rel='canonical']")).toBeNull();
  });
});

/**
 * The tests above prove the service does the right thing with the data it is handed. They cannot
 * prove the routes hand it the right data, because they declare their own routes to do it — which
 * is how eight pages were added carrying no `documentType` at all, each one silently republishing
 * the product entity the split exists to keep on the home page.
 *
 * A route table is the wrong place for a rule that lives in a comment. This reads the real one.
 */
describe('app routes as SEO input', () => {
  function indexable(): { path: string; seo: SeoData }[] {
    const collected: { path: string; seo: SeoData }[] = [];

    const walk = (children: readonly Route[], prefix: string): void => {
      for (const route of children) {
        // The wildcard redirect renders nothing and is never a destination.
        if (route.path === undefined || route.path === '**') {
          continue;
        }

        const path = [prefix, route.path].filter((part) => part.length > 0).join('/');
        const seo = route.data?.['seo'] as SeoData | undefined;
        if (seo !== undefined && seo.noIndex !== true) {
          collected.push({ path, seo });
        }

        if (route.children !== undefined) {
          walk(route.children, path);
        }
      }
    };

    walk(routes, '');
    return collected;
  }

  it('finds the routes it is meant to be checking', () => {
    // Without this the suite below passes just as well on an empty list, which is the failure mode
    // it exists to rule out.
    const paths = indexable().map((route) => route.path);
    expect(paths).toContain('');
    expect(paths).toContain('docs');
    expect(paths).toContain('compare');
    expect(paths.length).toBeGreaterThanOrEqual(13);
    expect(paths).not.toContain('workbench');
  });

  it('gives every indexable page a description a search result can show', () => {
    for (const { path, seo } of indexable()) {
      expect(seo.description.length, `/${path} has no description`).toBeGreaterThan(0);
      expect(seo.description.length, `/${path} description is too long to survive truncation`).
        toBeLessThanOrEqual(170);
    }
  });

  it('publishes every page except the home page as a document about the product', () => {
    for (const { path, seo } of indexable()) {
      if (path === '') {
        // The home page is the product's own page; an article here would compete with itself.
        expect(seo.documentType, 'the home page must not be an article').toBeUndefined();
        expect(seo.breadcrumb, 'the home page is the breadcrumb root').toBeUndefined();
        continue;
      }

      expect(
        seo.documentType,
        `/${path} declares no documentType, so it republishes the product entity`,
      ).toBeDefined();
      expect(seo.breadcrumb, `/${path} declares no breadcrumb`).toBeDefined();
      expect(seo.breadcrumb?.length ?? 0).toBeGreaterThan(0);
    }
  });
});
