import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BrandMarkComponent } from './brand-mark.component';
import { MarkdownPage } from './markdown-page';
import content from '../../../../docs/runbooks/DISASTER-RECOVERY.md';

/** Recovery boundaries, full-state and catalog recovery, validation, and drill procedures. */
@Component({
  selector: 'lh-disaster-recovery',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandMarkComponent, RouterLink],
  templateUrl: './operational-docs.component.html',
  styleUrls: ['./markdown-page.css', './site-header.css', './docs.component.css'],
})
export class DisasterRecoveryComponent extends MarkdownPage {
  constructor() {
    super(content, { repositoryDirectory: 'docs/runbooks' });
  }
}
