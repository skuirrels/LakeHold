import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BrandMarkComponent } from './brand-mark.component';
import { ThemeToggleComponent } from './theme-toggle.component';
import { MarkdownPage } from './markdown-page';
import content from '../../../../docs/runbooks/MONITORING-AND-ALERTING.md';

/** Health semantics, telemetry, dashboards, alert policy, and alert-validation procedures. */
@Component({
  selector: 'lh-monitoring',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandMarkComponent, RouterLink, ThemeToggleComponent],
  templateUrl: './operational-docs.component.html',
  styleUrls: ['./markdown-page.css', './site-header.css', './docs.component.css'],
})
export class MonitoringComponent extends MarkdownPage {
  constructor() {
    super(content, { repositoryDirectory: 'docs/runbooks' });
  }
}
