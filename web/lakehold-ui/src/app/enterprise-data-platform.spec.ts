import { describe, expect, it } from 'vitest';
import connectors from '../../../../docs/CONNECTORS.md';
import enterpriseDataPlatform from '../../../../docs/ENTERPRISE-DATA-PLATFORM.md';
import roadmap from '../../../../docs/ENTERPRISE-DATA-PLATFORM-ROADMAP.md';
import { routes } from './app.routes';
import { renderMarkdown, resolveMarkdownHref } from './markdown-page';

const edpRoutes = [
  {
    path: 'enterprise-data-platform',
    title: 'Enterprise Data Platform — LakeHold',
    content: enterpriseDataPlatform,
    heading: 'lakehold-as-an-enterprise-data-platform',
  },
  {
    path: 'docs/connectors',
    title: 'Managed REST and gRPC connectors — LakeHold documentation',
    content: connectors,
    heading: 'managed-data-connectors',
  },
  {
    path: 'docs/enterprise-data-platform-roadmap',
    title: 'Enterprise Data Platform delivery plan — LakeHold',
    content: roadmap,
    heading: 'enterprise-data-platform-delivery-plan',
  },
];

describe('Enterprise Data Platform documentation', () => {
  it.each(edpRoutes)('publishes $path from its repository Markdown source', (expected) => {
    const rendered = renderMarkdown(expected.content, { repositoryDirectory: 'docs' });
    const route = routes.find((candidate) => candidate.path === expected.path);

    expect(rendered.html).toContain(`<h1 id="${expected.heading}">`);
    expect(rendered.sections.length).toBeGreaterThan(2);
    expect(route?.title).toBe(expected.title);
    expect(route?.data?.['seo']?.description).toBeTruthy();
    expect(route?.data?.['seo']?.noIndex).not.toBe(true);
    expect(route?.loadComponent).toBeTypeOf('function');
  });

  it('renders explicit implemented and outstanding plan sections', () => {
    const rendered = renderMarkdown(roadmap, { repositoryDirectory: 'docs' }).html;

    expect(rendered).toContain('id="completed-in-source"');
    expect(rendered).toContain('id="not-completed"');
    expect(rendered).toContain('not included in the current v1.2.0 artifact');
  });

  it('turns EDP repository cross-links into native website routes', () => {
    const rendered = renderMarkdown(enterpriseDataPlatform, {
      repositoryDirectory: 'docs',
    }).html;

    expect(rendered).toContain('href="/docs/connectors"');
    expect(rendered).toContain('href="/docs/enterprise-data-platform-roadmap"');
    expect(resolveMarkdownHref('ENTERPRISE-DATA-PLATFORM.md', 'docs')).toBe(
      '/enterprise-data-platform',
    );
  });
});
