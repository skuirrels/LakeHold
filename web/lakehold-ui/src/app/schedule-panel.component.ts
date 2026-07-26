import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { formatTime } from './format';
import { LakehouseService } from './lakehouse.service';
import { ScheduledRun } from './models';
import { PanelErrorComponent } from './panel-error.component';

/**
 * What the scheduler actually did.
 *
 * The only panel with no tenant or catalog input: the run log is instance-wide, and the server
 * narrows the rows to what the credential may see. That is what makes it able to answer "did last
 * night's backup run" across every catalog at once, which is the question worth asking.
 */
@Component({
  selector: 'lh-schedule-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PanelErrorComponent],
  template: `
    <lh-panel-error title="Could not load scheduled runs" [message]="error()" />

    @if (!error() && runs().length === 0) {
      <p class="placeholder">
        No scheduled runs recorded yet. The scheduler flushes, compacts, and backs up on its own
        timers; this is the log of what it actually did.
      </p>
    } @else {
      <table>
        <thead>
          <tr>
            <th scope="col">Job</th>
            <th scope="col">Workspace</th>
            <th scope="col">Catalog</th>
            <th scope="col">Started</th>
            <th scope="col" class="num">Took</th>
            <th scope="col">Outcome</th>
          </tr>
        </thead>
        <tbody>
          @for (run of runs(); track run.job + run.tenant + run.catalog + run.startedUtc) {
            <tr>
              <td>{{ run.job }}</td>
              <td>{{ run.tenant }}</td>
              <td>{{ run.catalog }}</td>
              <td>{{ formatTime(run.startedUtc) }}</td>
              <td class="num">{{ run.elapsedMilliseconds.toFixed(0) }} ms</td>
              <td [title]="run.detail">
                @if (run.succeeded) {
                  <span class="ok-text">{{ run.detail }}</span>
                } @else {
                  <span class="warn-text">{{ run.detail }}</span>
                }
              </td>
            </tr>
          }
        </tbody>
      </table>
    }
  `,
  styleUrls: ['./panel-shared.css'],
  styles: [':host { display: block; }'],
})
export class SchedulePanelComponent {
  private readonly api = inject(LakehouseService);

  protected readonly runs = signal<ScheduledRun[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly formatTime = formatTime;

  constructor() {
    this.reload();
  }

  reload(): void {
    this.api.getScheduledRuns().subscribe({
      next: (runs) => this.runs.set(runs),
      error: (err: Error) => this.error.set(err.message),
    });
  }
}
