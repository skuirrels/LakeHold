import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import content from '../../../../docs/CONNECTORS.md';
import { BrandMarkComponent } from './brand-mark.component';
import { ThemeToggleComponent } from './theme-toggle.component';
import { MarkdownPage } from './markdown-page';

/** Website-rendered operator and integration documentation for managed REST/gRPC connectors. */
@Component({
  selector: 'lh-managed-connectors-docs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandMarkComponent, RouterLink, ThemeToggleComponent],
  templateUrl: './product-document.component.html',
  styleUrls: ['./markdown-page.css', './site-header.css', './docs.component.css'],
})
export class ManagedConnectorsDocsComponent extends MarkdownPage {
  protected readonly eyebrow = 'Managed connectors';
  protected readonly ctaTitle = 'See where connectors go next';
  protected readonly ctaBody =
    'The EDP plan separates the completed foundation from the adapter platform still to build.';
  protected readonly ctaRoute = '/docs/enterprise-data-platform-roadmap';
  protected readonly ctaLabel = 'Read the EDP plan';

  constructor() {
    super(content, { repositoryDirectory: 'docs' });
  }
}
