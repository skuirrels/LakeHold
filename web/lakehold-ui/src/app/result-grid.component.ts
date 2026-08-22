import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  OnChanges,
  signal,
} from '@angular/core';
import { Column, QueryResponse } from './models';

/** Searchable, exportable renderer for the bounded rows returned by one query. */
@Component({
  selector: 'lh-result-grid',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (result(); as data) {
      @if (data.columns.length === 0) {
        <div class="empty">Statement completed. No rows returned.</div>
      } @else {
        <div class="result-shell">
          <div class="result-toolbar">
            <label class="result-search">
              <span class="visually-hidden">Find in returned rows</span>
              <input
                type="search"
                placeholder="Find in returned rows…"
                [value]="filter()"
                (input)="filter.set($any($event.target).value)"
              />
            </label>
            <div class="column-picker">
              <button
                class="tool-btn"
                type="button"
                aria-haspopup="true"
                [attr.aria-expanded]="columnsOpen()"
                (click)="columnsOpen.update((open) => !open)"
              >
                Columns
              </button>
              @if (columnsOpen()) {
                <div class="column-menu">
                  @for (column of data.columns; track $index; let columnIndex = $index) {
                    <label
                      ><input
                        type="checkbox"
                        [checked]="!hiddenColumns().has(columnIndex)"
                        (change)="toggleColumn(columnIndex)"
                      /><span>{{ column.name }}</span></label
                    >
                  }
                </div>
              }
            </div>
            <button class="tool-btn" type="button" (click)="copyRows()">Copy rows</button>
            <button class="tool-btn" type="button" (click)="downloadCsv()">Download CSV</button>
            @if (notice()) {
              <span class="tool-notice" role="status">{{ notice() }}</span>
            }
          </div>

          @let columns = visibleColumns();
          @let rows = visibleRows();
          @let align = alignRight();
          <div class="grid-scroll">
            <table>
              <thead>
                <tr>
                  <th class="gutter" scope="col">#</th>
                  @for (entry of columns; track entry.index) {
                    <th scope="col" [class.numeric]="align[entry.index]">
                      <span class="column-heading"
                        ><span class="type-icon" aria-hidden="true">{{
                          typeIcon(entry.column, entry.index)
                        }}</span>
                        <span
                          ><span class="col-name">{{ entry.column.name }}</span
                          ><span class="col-type">{{ entry.column.dataType }}</span></span
                        >
                      </span>
                    </th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (row of rows; track row.index) {
                  <tr>
                    <td class="gutter">{{ row.index + 1 }}</td>
                    @for (entry of columns; track entry.index) {
                      @let cell = row.values[entry.index];
                      <td [class.numeric]="align[entry.index]" [class.null]="cell === null">
                        <button
                          type="button"
                          class="cell-copy"
                          [title]="'Copy ' + entry.column.name"
                          (click)="copyValue(cell)"
                        >
                          {{ render(cell) }}
                        </button>
                      </td>
                    }
                  </tr>
                } @empty {
                  <tr>
                    <td class="no-match" [attr.colspan]="columns.length + 1">
                      No returned rows match this search.
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <footer class="result-footer">
            <span
              >{{ rows.length.toLocaleString() }} of
              {{ data.rows.length.toLocaleString() }} returned rows</span
            >
            <span>{{ data.elapsedMilliseconds.toFixed(1) }} ms</span>
            @if (data.truncated) {
              <span class="truncated">Row limit reached</span>
            }
          </footer>
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
      .result-shell {
        height: 100%;
        display: grid;
        grid-template-rows: auto minmax(0, 1fr) auto;
      }
      .result-toolbar {
        min-height: 42px;
        padding: 6px 10px;
        display: flex;
        align-items: center;
        gap: 7px;
        background: var(--surface-1);
        border-bottom: 1px solid var(--border);
      }
      .result-search {
        flex: 1;
        max-width: 440px;
      }
      .result-search input {
        width: 100%;
        height: 30px;
        padding: 5px 9px;
        color: var(--text);
        background: var(--surface-2);
        border: 1px solid var(--border-strong);
        border-radius: var(--radius-sm);
        font: 12px var(--sans);
      }
      .tool-btn {
        height: 30px;
        padding: 4px 9px;
        color: var(--text-muted);
        background: var(--surface-2);
        border: 1px solid var(--border);
        border-radius: var(--radius-sm);
        font-size: 11px;
      }
      .tool-btn:hover {
        color: var(--text);
        border-color: var(--border-strong);
      }
      .tool-notice {
        color: var(--ok);
        font-size: 11px;
      }
      .column-picker {
        position: relative;
      }
      .column-menu {
        position: absolute;
        top: calc(100% + 5px);
        right: 0;
        z-index: 4;
        min-width: 210px;
        max-height: 280px;
        overflow: auto;
        padding: 6px;
        background: var(--surface-1);
        border: 1px solid var(--border-strong);
        border-radius: var(--radius-sm);
        box-shadow: var(--shadow-popover);
      }
      .column-menu label {
        display: flex;
        align-items: center;
        gap: 7px;
        padding: 5px 6px;
        color: var(--text-muted);
        font-size: 11px;
      }
      .column-menu input {
        accent-color: var(--accent);
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
      .column-heading {
        display: flex;
        align-items: center;
        gap: 7px;
      }
      .type-icon {
        min-width: 22px;
        height: 22px;
        display: inline-grid;
        place-items: center;
        color: var(--info);
        background: color-mix(in srgb, var(--info) 10%, transparent);
        border-radius: 4px;
        font: 9px var(--sans);
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
        padding: 0;
        border-bottom: 1px solid var(--border);
        white-space: nowrap;
        max-width: 420px;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      tbody tr:hover td {
        background: var(--surface-1);
      }
      .cell-copy {
        display: block;
        width: 100%;
        max-width: 420px;
        padding: 4px 12px;
        overflow: hidden;
        color: inherit;
        font: inherit;
        text-align: inherit;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .cell-copy:hover {
        color: var(--accent);
      }
      .numeric {
        text-align: right;
        font-variant-numeric: tabular-nums;
      }
      .null {
        color: var(--text-faint);
        font-style: italic;
      }
      .gutter {
        width: 44px;
        padding: 4px 10px;
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
      .no-match {
        padding: 20px;
        color: var(--text-faint);
        text-align: center;
      }
      .result-footer {
        min-height: 34px;
        padding: 7px 11px;
        display: flex;
        align-items: center;
        gap: 14px;
        color: var(--text-faint);
        background: var(--surface-1);
        border-top: 1px solid var(--border);
        font-size: 11px;
      }
      .truncated {
        color: var(--warn);
      }
      .visually-hidden {
        position: absolute;
        width: 1px;
        height: 1px;
        padding: 0;
        margin: -1px;
        overflow: hidden;
        clip: rect(0, 0, 0, 0);
        white-space: nowrap;
        border: 0;
      }
      @media (max-width: 720px) {
        .result-toolbar {
          flex-wrap: wrap;
        }
        .result-search {
          min-width: 100%;
          max-width: none;
        }
      }
    `,
  ],
})
export class ResultGridComponent implements OnChanges {
  readonly result = input.required<QueryResponse | null>();
  protected readonly filter = signal('');
  protected readonly hiddenColumns = signal(new Set<number>());
  protected readonly columnsOpen = signal(false);
  protected readonly notice = signal<string | null>(null);

  protected readonly alignRight = computed<boolean[]>(() => {
    const numeric = new Set([
      'Byte',
      'SByte',
      'Int16',
      'UInt16',
      'Int32',
      'UInt32',
      'Int64',
      'UInt64',
      'Single',
      'Double',
      'Decimal',
      'BigInteger',
    ]);
    return this.result()?.columns.map((column) => numeric.has(column.clrType)) ?? [];
  });
  protected readonly visibleColumns = computed(() =>
    (this.result()?.columns ?? [])
      .map((column, index) => ({ column, index }))
      .filter(({ index }) => !this.hiddenColumns().has(index)),
  );
  protected readonly visibleRows = computed(() => {
    const term = this.filter().trim().toLowerCase();
    return (this.result()?.rows ?? [])
      .map((values, index) => ({ values, index }))
      .filter(
        ({ values }) =>
          !term || values.some((value) => this.render(value).toLowerCase().includes(term)),
      );
  });

  ngOnChanges(): void {
    // Search and column choices describe one result shape. Carrying them into the next execution
    // can hide every row or the wrong positional column, especially when the query shape changes.
    this.filter.set('');
    this.hiddenColumns.set(new Set<number>());
    this.columnsOpen.set(false);
    this.notice.set(null);
  }

  protected toggleColumn(index: number): void {
    const next = new Set(this.hiddenColumns());
    if (!next.delete(index)) next.add(index);
    this.hiddenColumns.set(next);
  }
  protected typeIcon(column: Column, index: number): string {
    if (/date|time/i.test(column.dataType)) return '◷';
    if (/struct|map|list|array|json/i.test(column.dataType)) return '{}';
    if (this.alignRight()[index]) return '#';
    if (/bool/i.test(column.dataType)) return '01';
    return 'Ab';
  }
  protected async copyValue(value: unknown): Promise<void> {
    await this.writeClipboard(this.render(value), 'Cell copied');
  }
  protected async copyRows(): Promise<void> {
    const columns = this.visibleColumns();
    const lines = [
      columns.map(({ column }) => column.name).join('\t'),
      ...this.visibleRows().map(({ values }) =>
        columns.map(({ index }) => this.render(values[index])).join('\t'),
      ),
    ];
    await this.writeClipboard(lines.join('\n'), 'Returned rows copied');
  }
  protected downloadCsv(): void {
    if (
      typeof document === 'undefined' ||
      typeof URL === 'undefined' ||
      typeof URL.createObjectURL !== 'function'
    )
      return;
    const columns = this.visibleColumns();
    const csv = [
      columns.map(({ column }) => csvCell(column.name)).join(','),
      ...this.visibleRows().map(({ values }) =>
        columns.map(({ index }) => csvCell(values[index])).join(','),
      ),
    ].join('\r\n');
    const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = 'lakehold-query-results.csv';
    anchor.click();
    URL.revokeObjectURL(url);
    this.flash('CSV downloaded');
  }
  protected render(value: unknown): string {
    if (value === null || value === undefined) return 'NULL';
    return typeof value === 'object' ? JSON.stringify(value) : String(value);
  }
  private async writeClipboard(value: string, message: string): Promise<void> {
    if (typeof navigator === 'undefined' || !navigator.clipboard?.writeText) {
      this.flash('Clipboard unavailable');
      return;
    }

    try {
      await navigator.clipboard.writeText(value);
      this.flash(message);
    } catch {
      this.flash('Clipboard unavailable');
    }
  }
  private flash(message: string): void {
    this.notice.set(message);
    if (typeof window !== 'undefined') window.setTimeout(() => this.notice.set(null), 1800);
  }
}

function csvCell(value: unknown): string {
  const rendered =
    value === null || value === undefined
      ? ''
      : typeof value === 'object'
        ? JSON.stringify(value)
        : String(value);
  return /[",\r\n]/.test(rendered) ? `"${rendered.replaceAll('"', '""')}"` : rendered;
}
