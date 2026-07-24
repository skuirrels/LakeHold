import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SeoService } from './seo.service';

/**
 * Shell.
 *
 * Previously a two-view signal toggle, which was fine while the app had exactly two surfaces and
 * no shareable state. The comparison page changes that: a page whose whole purpose is being linked
 * to needs a real URL, so the router now owns navigation.
 */
@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet],
  template: '<router-outlet />',
})
export class App {
  constructor() {
    // The shell outlives every route, so this is the one place the per-route meta tags can be
    // driven from a single subscription.
    inject(SeoService).init();
  }
}
