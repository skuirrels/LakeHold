import { describe, expect, it } from 'vitest';
import operations from '../../../../docs/OPERATIONS.md';
import disasterRecovery from '../../../../docs/runbooks/DISASTER-RECOVERY.md';
import incidentResponse from '../../../../docs/runbooks/INCIDENT-RESPONSE.md';
import monitoring from '../../../../docs/runbooks/MONITORING-AND-ALERTING.md';
import { routes } from './app.routes';
import { renderMarkdown, resolveMarkdownHref } from './markdown-page';

const operationalRoutes = [
  { path: 'docs/operations', title: 'Operations — run LakeHold in production' },
  { path: 'docs/incident-response', title: 'Incident response — LakeHold operations' },
  { path: 'docs/disaster-recovery', title: 'Disaster recovery — LakeHold operations' },
  { path: 'docs/monitoring', title: 'Monitoring and alerting — LakeHold operations' },
];

describe('operational documentation', () => {
  it.each([
    {
      content: operations,
      directory: 'docs',
      heading: 'operating-lakehold',
    },
    {
      content: incidentResponse,
      directory: 'docs/runbooks',
      heading: 'incident-response-runbook',
    },
    {
      content: disasterRecovery,
      directory: 'docs/runbooks',
      heading: 'disaster-recovery-runbook',
    },
    {
      content: monitoring,
      directory: 'docs/runbooks',
      heading: 'monitoring-and-alerting',
    },
  ])('renders the repository source containing # $heading', ({ content, directory, heading }) => {
    const rendered = renderMarkdown(content, { repositoryDirectory: directory });

    expect(rendered.html).toContain(`<h1 id="${heading}">`);
    expect(rendered.sections.length).toBeGreaterThan(3);
  });

  it('publishes all four documents as titled, indexable routes', () => {
    for (const expected of operationalRoutes) {
      const route = routes.find((candidate) => candidate.path === expected.path);

      expect(route, expected.path).toBeDefined();
      expect(route?.title).toBe(expected.title);
      expect(route?.data?.['seo']?.description).toBeTruthy();
      expect(route?.data?.['seo']?.noIndex).not.toBe(true);
      expect(route?.loadComponent).toBeTypeOf('function');
    }
  });

  it('turns operational cross-links into native site routes', () => {
    const rendered = renderMarkdown(operations, { repositoryDirectory: 'docs' }).html;

    expect(rendered).toContain('href="/docs/incident-response"');
    expect(rendered).toContain('href="/docs/disaster-recovery"');
    expect(rendered).toContain('href="/docs/monitoring"');
  });

  it('preserves fragments when an operational link becomes a native route', () => {
    expect(resolveMarkdownHref('../OPERATIONS.md#routine-operations', 'docs/runbooks')).toBe(
      '/docs/operations#routine-operations',
    );
    expect(resolveMarkdownHref('INCIDENT-RESPONSE.md#resource-exhaustion', 'docs/runbooks')).toBe(
      '/docs/incident-response#resource-exhaustion',
    );
  });

  it('sends supporting repository documents to their exact GitHub source', () => {
    expect(resolveMarkdownHref('../EXIT-PATH.md#full-migration-checklist', 'docs/runbooks')).toBe(
      'https://github.com/skuirrels/LakeHold/blob/main/docs/EXIT-PATH.md#full-migration-checklist',
    );
  });

  it('does not rewrite external, fragment, or non-Markdown links', () => {
    expect(resolveMarkdownHref('https://duckdb.org/docs', 'docs')).toBe('https://duckdb.org/docs');
    expect(resolveMarkdownHref('#supported-operating-profile', 'docs')).toBe(
      '#supported-operating-profile',
    );
    expect(resolveMarkdownHref('/workbench', 'docs')).toBe('/workbench');
  });

  it('keeps canonical documentation links portable on GitHub and native on the site', () => {
    expect(resolveMarkdownHref('https://lakehold.dev/docs/operations')).toBe('/docs/operations');
    expect(resolveMarkdownHref('https://lakehold.dev/docs/disaster-recovery#restore-drills')).toBe(
      '/docs/disaster-recovery#restore-drills',
    );
  });
});
