import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { QueryResponse } from './models';

/** Tabular renderer for a query result. */
@Component({
  selector: 'lh-result-grid',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (result(); as data) {
      @if (data.columns.length === 0) {
        <!-- DDL and DML return no columns. The provider's dynamic reader does not surface an
             affected-row count, so this reports completion rather than inventing a number. -->
        <div class="empty">Statement completed. No rows returned.</div>
      } @else {
        <!-- Read the per-column alignment once per render rather than re-invoking the computed in
             every header and body cell, which on a full result is tens of thousands of calls. -->
        @let align = alignRight();
        <div class="grid-scroll">
          <table>
            <thead>
              <tr>
                <th class="gutter" scope="col">#</th>
                @for (column of data.columns; track column.name; let i = $index) {
                  <th scope="col" [class.numeric]="align[i]">
                    <span class="col-name">{{ column.name }}</span>
                    <span class="col-type">{{ column.dataType }}</span>
                  </th>
                }
              </tr>
            </thead>
            <tbody>
              @for (row of data.rows; track $index; let r = $index) {
                <tr>
                  <td class="gutter">{{ r + 1 }}</td>
                  @for (cell of row; track $index; let i = $index) {
                    <td [class.numeric]="align[i]" [class.null]="cell === null">
                      {{ render(cell) }}
                    </td>
                  }
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    }
  `,
  styles: [
    `
      :host {
        display: block;
        height: 100%;
        overflow: hidden;
      }

      .grid-scroll {
        height: 100%;
        overflow: auto;
      }

      .empty {
        padding: 18px;
        color: var(--text-muted);
        font-size: 13px;
      }

      table {
        border-collapse: separate;
        border-spacing: 0;
        width: max-content;
        min-width: 100%;
        font-family: var(--mono);
        font-size: 12px;
      }

      thead th {
        position: sticky;
        top: 0;
        z-index: 1;
        background: var(--surface-2);
        border-bottom: 1px solid var(--border-strong);
        padding: 6px 12px;
        text-align: left;
        white-space: nowrap;
        vertical-align: bottom;
      }

      .col-name {
        display: block;
        color: var(--text);
        font-weight: 600;
      }

      .col-type {
        display: block;
        color: var(--text-faint);
        font-size: 10px;
        font-weight: 400;
        text-transform: lowercase;
      }

      tbody td {
        padding: 4px 12px;
        border-bottom: 1px solid var(--border);
        white-space: nowrap;
        max-width: 420px;
        overflow: hidden;
        text-overflow: ellipsis;
      }

      tbody tr:hover td {
        background: var(--surface-1);
      }

      /* Numeric columns right-align so magnitudes line up and are scannable. */
      .numeric {
        text-align: right;
        font-variant-numeric: tabular-nums;
      }

      .null {
        color: var(--text-faint);
        font-style: italic;
      }

      .gutter {
        color: var(--text-faint);
        text-align: right;
        user-select: none;
        background: var(--surface-1);
        position: sticky;
        left: 0;
        border-right: 1px solid var(--border);
      }

      thead .gutter {
        z-index: 2;
      }
    `,
  ],
})
export class ResultGridComponent {
  readonly result = input.required<QueryResponse | null>();

  /**
   * Per-column right-alignment.
   *
   * Driven by the CLR type the provider reports, which is an exact set rather than the pattern
   * match over DuckDB type names this previously used — that older test also matched `INTERVAL`
   * and would have matched a `STRUCT(int)`. Deriving alignment from the declared type rather than
   * from values also avoids misaligning a column whose first page is all NULL, or right-aligning a
   * VARCHAR that merely contains digits.
   */
  protected readonly alignRight = computed<boolean[]>(() => {
    const numeric = new Set([
      'Byte', 'SByte', 'Int16', 'UInt16', 'Int32', 'UInt32', 'Int64', 'UInt64',
      'Single', 'Double', 'Decimal', 'BigInteger',
    ]);

    return this.result()?.columns.map((column) => numeric.has(column.clrType)) ?? [];
  });

  protected render(value: unknown): string {
    if (value === null || value === undefined) {
      return 'NULL';
    }

    if (typeof value === 'object') {
      // LIST, STRUCT, and MAP columns arrive as arrays and objects.
      return JSON.stringify(value);
    }

    return String(value);
  }
}
