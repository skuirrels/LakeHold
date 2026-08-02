import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import content from '../../../../docs/ENTERPRISE-DATA-PLATFORM-ROADMAP.md';
import { BrandMarkComponent } from './brand-mark.component';
import { MarkdownPage } from './markdown-page';

/** Website-rendered EDP delivery plan, including explicit completed and outstanding checklists. */
@Component({
  selector: 'lh-enterprise-data-platform-roadmap',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandMarkComponent, RouterLink],
  templateUrl: './product-document.component.html',
  styleUrls: ['./markdown-page.css', './docs.component.css'],
})
export class EnterpriseDataPlatformRoadmapComponent extends MarkdownPage {
  protected readonly eyebrow = 'Enterprise Data Platform plan';
  protected readonly ctaTitle = 'Understand the current platform';
  protected readonly ctaBody =
    'Read the EDP capability overview and its honest product boundaries.';
  protected readonly ctaRoute = '/enterprise-data-platform';
  protected readonly ctaLabel = 'Explore the EDP overview';

  constructor() {
    super(content, { repositoryDirectory: 'docs' });
  }
}
