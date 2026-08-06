import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BrandMarkComponent } from './brand-mark.component';
import { MarkdownPage } from './markdown-page';
import content from '../../../../docs/LINQ_WORKBENCH.md';

/** Canonical C# LINQ Workbench guide, rendered directly from the repository documentation. */
@Component({
  selector: 'lh-linq-workbench-docs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandMarkComponent, RouterLink],
  templateUrl: './linq-workbench-docs.component.html',
  styleUrls: ['./markdown-page.css', './site-header.css', './docs.component.css'],
})
export class LinqWorkbenchDocsComponent extends MarkdownPage {
  constructor() {
    super(content, { repositoryDirectory: 'docs' });
  }
}
