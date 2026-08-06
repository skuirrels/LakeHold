import { afterNextRender, DestroyRef, ElementRef, inject, signal } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { ActivatedRoute } from '@angular/router';
import { Marked, Renderer } from 'marked';

/** A jump target in the left navigation, built from a rendered heading. */
export interface NavLink {
  id: string;
  label: string;
}

/** A top-level (`h2`) section and its `h3` children. */
export interface NavSection extends NavLink {
  children: NavLink[];
}

/** Context needed to turn repository-relative Markdown links into deployable site links. */
export interface MarkdownPageOptions {
  /**
   * Directory containing the source document, relative to the repository root.
   *
   * When set, links to published repository documents become native site routes. Other
   * relative Markdown links keep working by resolving to their source on GitHub instead of becoming
   * broken paths relative to the deployed page.
   */
  repositoryDirectory?: string;
}

const repositoryDocumentUrl = 'https://github.com/skuirrels/LakeHold/blob/main';

const nativeDocumentRoutes = new Map<string, string>([
  ['ENTERPRISE-DATA-PLATFORM.md', '/enterprise-data-platform'],
  ['ENTERPRISE-DATA-PLATFORM-ROADMAP.md', '/docs/enterprise-data-platform-roadmap'],
  ['CONNECTORS.md', '/docs/connectors'],
  ['OPERATIONS.md', '/docs/operations'],
  ['LINQ_WORKBENCH.md', '/docs/linq-workbench'],
  ['INCIDENT-RESPONSE.md', '/docs/incident-response'],
  ['DISASTER-RECOVERY.md', '/docs/disaster-recovery'],
  ['MONITORING-AND-ALERTING.md', '/docs/monitoring'],
]);

/** Resolves `.` and `..` without pulling Node's path module into the browser bundle. */
function resolveRepositoryPath(directory: string, target: string): string {
  const resolved: string[] = [];
  for (const part of `${directory}/${target}`.split('/')) {
    if (!part || part === '.') {
      continue;
    }
    if (part === '..') {
      resolved.pop();
    } else {
      resolved.push(part);
    }
  }
  return resolved.join('/');
}

/**
 * Maps a repository Markdown link to either its native documentation route or its GitHub source.
 * External URLs, fragments, and non-Markdown assets pass through unchanged.
 */
export function resolveMarkdownHref(href: string, repositoryDirectory?: string): string {
  const canonicalDocumentationOrigin = 'https://lakehold.dev';
  if (href.startsWith(`${canonicalDocumentationOrigin}/docs/`)) {
    return href.slice(canonicalDocumentationOrigin.length);
  }

  if (
    !repositoryDirectory ||
    href.startsWith('#') ||
    /^[a-z][a-z0-9+.-]*:/i.test(href) ||
    href.startsWith('//')
  ) {
    return href;
  }

  const hashAt = href.indexOf('#');
  const target = hashAt === -1 ? href : href.slice(0, hashAt);
  const fragment = hashAt === -1 ? '' : href.slice(hashAt);
  if (!target.toLowerCase().endsWith('.md')) {
    return href;
  }

  const fileName = target.split('/').at(-1) ?? target;
  const nativeRoute = nativeDocumentRoutes.get(fileName);
  if (nativeRoute) {
    return `${nativeRoute}${fragment}`;
  }

  return `${repositoryDocumentUrl}/${resolveRepositoryPath(repositoryDirectory, target)}${fragment}`;
}

/**
 * Renders a Markdown document once, assigning a stable id to every heading and collecting the `h2`/
 * `h3` outline the sidebar is built from. Deriving the navigation from the content keeps the single
 * source of truth in the Markdown file: add a section there and it appears in the sidebar for free.
 */
export function renderMarkdown(
  content: string,
  options: MarkdownPageOptions = {},
): { html: string; sections: NavSection[] } {
  const renderer = new Renderer();
  const outline: { id: string; label: string; depth: number }[] = [];
  const used = new Map<string, number>();

  const slug = (raw: string): string => {
    const base =
      raw
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-+|-+$/g, '') || 'section';
    const seen = used.get(base);
    if (seen === undefined) {
      used.set(base, 0);
      return base;
    }
    const next = seen + 1;
    used.set(base, next);
    return `${base}-${next}`;
  };

  renderer.heading = function heading({ depth, tokens, text }) {
    const inner = this.parser.parseInline(tokens);
    const plain = text.replace(/[*`_]/g, '').trim();
    const id = slug(plain);
    if (depth === 2 || depth === 3) {
      // The `h3` location tag (" — top bar") is noise in a narrow rail, so the label is the part
      // before the em dash; the id still slugs the whole heading so the anchor matches the content.
      const label = depth === 3 ? plain.split(' — ')[0].trim() : plain;
      outline.push({ id, label, depth });
    }
    return `<h${depth} id="${id}">${inner}</h${depth}>\n`;
  };

  renderer.link = function link({ href, title, tokens }) {
    const resolvedHref = resolveMarkdownHref(href, options.repositoryDirectory);
    const titleAttribute = title ? ` title="${title.replaceAll('"', '&quot;')}"` : '';
    return `<a href="${resolvedHref.replaceAll('"', '&quot;')}"${titleAttribute}>${this.parser.parseInline(tokens)}</a>`;
  };

  const html = new Marked({ renderer }).parse(content, { async: false }) as string;

  const sections: NavSection[] = [];
  for (const entry of outline) {
    if (entry.depth === 2) {
      sections.push({ id: entry.id, label: entry.label, children: [] });
    } else if (sections.length > 0) {
      sections[sections.length - 1].children.push({ id: entry.id, label: entry.label });
    }
  }
  return { html, sections };
}

/**
 * The behaviour shared by every long-form page whose prose is compiled in from a Markdown file: the
 * rendered body, the outline the rail is built from, and the scroll-spy that keeps the two in step.
 *
 * The prose is authored in this repository and compiled into the bundle — not user input — so the
 * rendered HTML is trusted directly rather than run through the sanitizer, which would strip the
 * heading ids and table markup the sidebar and layout depend on.
 *
 * Subclasses must be constructed in an injection context, which a component always is.
 */
export abstract class MarkdownPage {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroyRef = inject(DestroyRef);
  private readonly route = inject(ActivatedRoute);

  protected readonly body: SafeHtml;
  protected readonly sections: NavSection[];
  protected readonly activeId = signal('');

  protected constructor(content: string, options: MarkdownPageOptions = {}) {
    const { html, sections } = renderMarkdown(content, options);
    this.body = inject(DomSanitizer).bypassSecurityTrustHtml(html);
    this.sections = sections;
    afterNextRender(() => {
      this.trackActiveHeading();
      this.wireInContentAnchors();
      // A link from another page (`routerLink="/provider/docs" fragment="compatibility"`) has to land
      // on the section it names. The router's anchor scrolling works on the document scroller, and
      // the prose scrolls inside this component, so the fragment is applied here instead.
      const fragment = this.route.snapshot.fragment;
      if (fragment) {
        this.scrollTo(fragment);
      }
    });
  }

  /**
   * Routes the cross-links inside the rendered prose (`<a href="#section">`) through the same jump
   * handler the sidebar uses. They are injected as raw HTML and never bound by Angular, and native
   * fragment scrolling is unreliable in this nested scroll container, so they are delegated here.
   */
  private wireInContentAnchors(): void {
    const article = this.host.nativeElement.querySelector('.markdown');
    if (!article) {
      return;
    }
    const onClick = (event: Event): void => {
      const link = (event.target as HTMLElement).closest('a[href^="#"]');
      const href = link?.getAttribute('href');
      if (href && href.length > 1) {
        this.jumpTo(event, decodeURIComponent(href.slice(1)));
      }
    };
    article.addEventListener('click', onClick);
    this.destroyRef.onDestroy(() => article.removeEventListener('click', onClick));
  }

  /**
   * Scrolls to a section and marks it active immediately, ahead of the observer catching up.
   *
   * The target is computed against the scrolling host and passed to `scrollTo` rather than using
   * `scrollIntoView`, which is unreliable inside a nested scroll container. The scroll is instant:
   * a native smooth scroll depends on the animation-frame loop, which browsers pause for a hidden
   * tab, and a paused animation can leave the page stuck partway.
   */
  protected jumpTo(event: Event, id: string): void {
    event.preventDefault();
    this.scrollTo(id);
  }

  /** The scroll itself, shared by the sidebar links and by an incoming route fragment. */
  private scrollTo(id: string): void {
    const host = this.host.nativeElement;
    const target = host.querySelector(`#${CSS.escape(id)}`);
    if (target) {
      const top =
        host.scrollTop +
        target.getBoundingClientRect().top -
        host.getBoundingClientRect().top -
        16 -
        this.stickyHeaderHeight();
      host.scrollTo({ top, behavior: 'auto' });
    }
    this.activeId.set(id);
  }

  /**
   * How much of the scroll port the site header covers, so a jumped-to heading lands below the bar
   * instead of behind it. Measured rather than read from `--site-header-height`, because the header
   * stops being sticky at a narrow width and can wrap to a second row; both change the answer, and a
   * heading hidden under the bar reads as a link that went to the wrong place.
   */
  private stickyHeaderHeight(): number {
    const header = this.host.nativeElement.querySelector<HTMLElement>('.nav');
    if (!header || getComputedStyle(header).position !== 'sticky') {
      return 0;
    }
    return header.offsetHeight;
  }

  /**
   * Highlights the sidebar link for whichever heading is currently near the top of the scroll
   * container. The observer's root is the scrolling host, and the bottom margin narrows the
   * "active" band to the top slice of the viewport so the highlight tracks reading position.
   */
  private trackActiveHeading(): void {
    const root = this.host.nativeElement;
    const headings = Array.from(
      root.querySelectorAll<HTMLElement>('.markdown h2[id], .markdown h3[id]'),
    );
    if (headings.length === 0) {
      return;
    }

    const order = headings.map((h) => h.id);
    const visible = new Set<string>();
    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (entry.isIntersecting) {
            visible.add(entry.target.id);
          } else {
            visible.delete(entry.target.id);
          }
        }
        const top = order.find((id) => visible.has(id));
        if (top) {
          this.activeId.set(top);
        }
      },
      { root, rootMargin: '0px 0px -72% 0px', threshold: 0 },
    );

    headings.forEach((h) => observer.observe(h));
    this.activeId.set(order[0]);
    this.destroyRef.onDestroy(() => observer.disconnect());
  }
}
