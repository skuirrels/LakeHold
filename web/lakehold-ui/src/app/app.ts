import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

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
export class App {}
