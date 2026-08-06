import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BrandMarkComponent } from './brand-mark.component';
import { MarkdownPage } from './markdown-page';
import content from './provider.content.md';

/**
 * The DuckDB.EFCoreProvider reference documentation.
 *
 * It is a page of its own rather than a section of `/provider` so that "Docs" means one thing on
 * each surface: the provider pages link here, the LakeHold pages link to `/docs`. The prose lives in
 * `provider.content.md` and `MarkdownPage` supplies the rendering, the outline, and the scroll-spy,
 * exactly as the LakeHold docs page renders its own content.
 */
@Component({
  selector: 'lh-provider-docs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandMarkComponent, RouterLink],
  templateUrl: './provider-docs.component.html',
  styleUrls: ['./markdown-page.css', './site-header.css', './provider-docs.component.css'],
})
export class ProviderDocsComponent extends MarkdownPage {
  constructor() {
    super(content);
  }
}
