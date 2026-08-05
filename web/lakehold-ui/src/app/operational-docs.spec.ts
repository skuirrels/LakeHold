import { describe, expect, it } from 'vitest';
import operations from '../../../../docs/OPERATIONS.md';
import disasterRecovery from '../../../../docs/runbooks/DISASTER-RECOVERY.md';
import incidentResponse from '../../../../docs/runbooks/INCIDENT-RESPONSE.md';
import monitoring from '../../../../docs/runbooks/MONITORING-AND-ALERTING.md';
import linqWorkbench from '../../../../docs/LINQ_WORKBENCH.md';
import { routes } from './app.routes';
import { renderMarkdown, resolveMarkdownHref } from './markdown-page';

const operationalRoutes = [
  { path: 'docs/operations', title: 'Operations — run LakeHold in production' },
  { path: 'docs/incident-response', title: 'Incident response — LakeHold operations' },
  { path: 'docs/disaster-recovery', title: 'Disaster recovery — LakeHold operations' },
  { path: 'docs/monitoring', title: 'Monitoring and alerting — LakeHold operations' },
];

const repositoryDocumentationRoutes = [
  ...operationalRoutes,
  { path: 'docs/linq-workbench', title: 'C# LINQ Workbench — LakeHold documentation' },
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
    {
      content: linqWorkbench,
      directory: 'docs',
      heading: 'c-linq-in-the-workbench',
    },
  ])('renders the repository source containing # $heading', ({ content, directory, heading }) => {
    const rendered = renderMarkdown(content, { repositoryDirectory: directory });

    expect(rendered.html).toContain(`<h1 id="${heading}">`);
    expect(rendered.sections.length).toBeGreaterThan(3);
  });

  // A release once published only `1.2.3` while every operator instruction quoted `v1.2.3`, so
  // pinning a release exactly as documented answered `manifest unknown`. A placeholder such as
  // `LAKEHOLD_TAG=<pinned-tag>` hides that class of defect permanently: nobody can run it, so
  // nobody discovers the form is wrong. Every deploy example therefore names a real version, and
  // the surrounding prose says which value to substitute.
  it.each([
    { name: 'OPERATIONS.md', content: operations },
    { name: 'DISASTER-RECOVERY.md', content: disasterRecovery },
    { name: 'INCIDENT-RESPONSE.md', content: incidentResponse },
    { name: 'MONITORING-AND-ALERTING.md', content: monitoring },
  ])('pins $name deploy examples to a runnable version, not a placeholder', ({ content }) => {
    const assignments = [...content.matchAll(/LAKEHOLD_TAG=(\S+)/g)].map((match) => match[1]);

    for (const value of assignments) {
      expect(value, `LAKEHOLD_TAG=${value}`).toMatch(/^v?\d+\.\d+\.\d+$/);
    }
  });

  it('has deploy examples to check in the first place', () => {
    const documented = [operations, disasterRecovery].flatMap((content) => [
      ...content.matchAll(/LAKEHOLD_TAG=(\S+)/g),
    ]);

    expect(documented.length).toBeGreaterThanOrEqual(4);
  });

  it('publishes repository documents as titled, indexable routes', () => {
    for (const expected of repositoryDocumentationRoutes) {
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
    expect(resolveMarkdownHref('LINQ_WORKBENCH.md#query-shape', 'docs')).toBe(
      '/docs/linq-workbench#query-shape',
    );
  });
});
