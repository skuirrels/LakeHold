import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  inject,
  signal,
} from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';
import { Marked, Renderer } from 'marked';
import content from './docs.content.md';

/** A jump target in the left navigation, built from a rendered heading. */
interface NavLink {
  id: string;
  label: string;
}

/** A top-level (`h2`) section and its `h3` children. */
interface NavSection extends NavLink {
  children: NavLink[];
}

/**
 * Renders the docs Markdown once, assigning a stable id to every heading and collecting the `h2`/
 * `h3` outline the sidebar is built from. Deriving the navigation from the content keeps the single
 * source of truth in `docs.content.md`: add a section there and it appears in the sidebar for free.
 */
function renderDocs(): { html: string; sections: NavSection[] } {
  const renderer = new Renderer();
  const outline: { id: string; label: string; depth: number }[] = [];
  const used = new Map<string, number>();

  const slug = (raw: string): string => {
    const base = raw.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'section';
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
 * Documentation surface: how to run Lakehold, which tool to reach for, and what every feature in
 * the workbench and the API is for.
 *
 * The prose lives in `docs.content.md`, the single source shared with the copy read on GitHub. It
 * is authored in this repository and compiled into the bundle — not user input — so the rendered
 * HTML is trusted directly rather than run through the sanitizer, which would strip the heading ids
 * and table markup the sidebar and layout depend on.
 */
@Component({
  selector: 'lh-docs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  templateUrl: './docs.component.html',
  styleUrl: './docs.component.css',
})
export class DocsComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly body: SafeHtml;
  protected readonly sections: NavSection[];
  protected readonly activeId = signal('');

  constructor(sanitizer: DomSanitizer) {
    const { html, sections } = renderDocs();
    this.body = sanitizer.bypassSecurityTrustHtml(html);
    this.sections = sections;
    afterNextRender(() => {
      this.trackActiveHeading();
      this.wireInContentAnchors();
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
    const host = this.host.nativeElement;
    const target = host.querySelector(`#${CSS.escape(id)}`);
    if (target) {
      const top =
        host.scrollTop + target.getBoundingClientRect().top - host.getBoundingClientRect().top - 16;
      host.scrollTo({ top, behavior: 'auto' });
    }
    this.activeId.set(id);
  }

  /**
   * Highlights the sidebar link for whichever heading is currently near the top of the scroll
   * container. The observer's root is the scrolling host, and the bottom margin narrows the
   * "active" band to the top slice of the viewport so the highlight tracks reading position.
   */
  private trackActiveHeading(): void {
    const root = this.host.nativeElement;
    const headings = Array.from(root.querySelectorAll<HTMLElement>('.markdown h2[id], .markdown h3[id]'));
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
