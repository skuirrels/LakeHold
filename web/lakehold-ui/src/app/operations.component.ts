import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BrandMarkComponent } from './brand-mark.component';
import { ThemeToggleComponent } from './theme-toggle.component';
import { MarkdownPage } from './markdown-page';
import content from '../../../../docs/OPERATIONS.md';

/** Production operating model and entry point for LakeHold's operational runbooks. */
@Component({
  selector: 'lh-operations',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandMarkComponent, RouterLink, ThemeToggleComponent],
  templateUrl: './operational-docs.component.html',
  styleUrls: ['./markdown-page.css', './site-header.css', './docs.component.css'],
})
export class OperationsComponent extends MarkdownPage {
  constructor() {
    super(content, { repositoryDirectory: 'docs' });
  }
}
