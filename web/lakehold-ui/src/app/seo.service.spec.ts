import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { SITE_ORIGIN, SeoService } from './seo.service';

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
