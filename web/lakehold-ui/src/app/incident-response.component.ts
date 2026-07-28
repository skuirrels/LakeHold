import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BrandMarkComponent } from './brand-mark.component';
import { MarkdownPage } from './markdown-page';
import content from '../../../../docs/runbooks/INCIDENT-RESPONSE.md';

/** Incident severity, first response, containment, recovery, and communication procedures. */
@Component({
  selector: 'lh-incident-response',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandMarkComponent, RouterLink],
  templateUrl: './operational-docs.component.html',
  styleUrls: ['./markdown-page.css', './docs.component.css'],
})
export class IncidentResponseComponent extends MarkdownPage {
  constructor() {
    super(content, { repositoryDirectory: 'docs/runbooks' });
  }
}
