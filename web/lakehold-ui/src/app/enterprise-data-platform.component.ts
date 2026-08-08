import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import content from '../../../../docs/ENTERPRISE-DATA-PLATFORM.md';
import { BrandMarkComponent } from './brand-mark.component';
import { ThemeToggleComponent } from './theme-toggle.component';
import { MarkdownPage } from './markdown-page';

/** Public product and capability description for LakeHold's Enterprise Data Platform direction. */
@Component({
  selector: 'lh-enterprise-data-platform',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandMarkComponent, RouterLink, ThemeToggleComponent],
  templateUrl: './product-document.component.html',
  styleUrls: ['./markdown-page.css', './site-header.css', './docs.component.css'],
})
export class EnterpriseDataPlatformComponent extends MarkdownPage {
  protected readonly eyebrow = 'Enterprise Data Platform';
  protected readonly ctaTitle = 'See the delivery status';
  protected readonly ctaBody =
    'Review exactly what is implemented, partial, unreleased, and not started.';
  protected readonly ctaRoute = '/docs/enterprise-data-platform-roadmap';
  protected readonly ctaLabel = 'Read the EDP plan';

  constructor() {
    super(content, { repositoryDirectory: 'docs' });
  }
}
