import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BrandMarkComponent } from './brand-mark.component';
import { MarkdownPage } from './markdown-page';
import content from './docs.content.md';

/**
 * Documentation surface: how to run LakeHold, which tool to reach for, and what every feature in
 * the workbench and the API is for.
 *
 * The prose lives in `docs.content.md`, the single source shared with the copy read on GitHub, and
 * `MarkdownPage` supplies the rendering, the outline, and the scroll-spy the rail is driven by.
 */
@Component({
  selector: 'lh-docs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandMarkComponent, RouterLink],
  templateUrl: './docs.component.html',
  styleUrls: ['./markdown-page.css', './site-header.css', './docs.component.css'],
})
export class DocsComponent extends MarkdownPage {
  constructor() {
    super(content);
  }
}
