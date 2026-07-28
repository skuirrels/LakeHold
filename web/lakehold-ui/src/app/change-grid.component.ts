import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ChangePage } from './models';

/**
 * Renders one bounded page from DuckLake's row-level change feed.
 *
 * Shared by the live Changes panel and the snapshot history drill-down so update-pair semantics,
 * dynamic columns, and truncation presentation cannot drift between two implementations.
 */
@Component({
  selector: 'lh-change-grid',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './change-grid.component.html',
  styleUrls: ['./panel-shared.css', './change-grid.component.css'],
})
export class ChangeGridComponent {
  readonly page = input.required<ChangePage>();

  /** Column names are carried by each dynamic row; the first non-empty row defines the grid. */
  protected readonly columns = computed(() => {
    const first = this.page().changes.find((change) => Object.keys(change.row).length > 0);
    return first ? Object.keys(first.row) : [];
  });
}
