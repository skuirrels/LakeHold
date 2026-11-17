import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  input,
  OnChanges,
  signal,
  viewChild,
} from '@angular/core';
import { Column, QueryResponse, Row } from './models';

/**
 * Every body row is exactly this tall, which is what lets the grid render only the rows in view
 * and stand in for the rest with two spacer rows of known height. The stylesheet pins the same
 * figure; change both together.
 */
const ROW_HEIGHT = 28;
/** Rows rendered beyond each edge of the viewport, so a fast scroll does not show bare spacer. */
const OVERSCAN = 10;
/** The viewport assumed before it has been measured, and wherever layout reports no height. */
const FALLBACK_VIEWPORT = 640;
/** Rows read to size the columns. Widths are fixed so they cannot jump as rows scroll in. */
const WIDTH_SAMPLE = 500;
const MIN_COLUMN_WIDTH = 72;
const MAX_COLUMN_WIDTH = 420;
/** Type badge, gap, cell padding, and sort mark beside a heading. */
const HEADING_CHROME = 22 + 7 + 24 + 16;
const CELL_PADDING = 24;

let gridInstances = 0;

interface GridRow {
  readonly values: Row;
  /** Position in the response, which is the row number shown whatever the sort. */
  readonly index: number;
}

/** `row` is a position in the sorted, filtered rows, or -1 for the heading row. */
interface ActiveCell {
  readonly row: number;
  readonly column: number;
}

interface SortOrder {
  readonly index: number;
  readonly direction: 'ascending' | 'descending';
}

/**
 * Searchable, sortable, exportable renderer for the bounded rows returned by one query.
 *
 * It is an ARIA grid with a single tab stop: arrow keys move between cells, Enter copies a cell
 * or sorts by a heading. Rows are virtualised, so a 10,000-row result renders a few dozen rows
 * rather than tens of thousands of elements.
 */
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
                (input)="search($any($event.target).value)"
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
          @let rows = sortedRows();
          @let align = alignRight();
          @let widths = columnWidths();
          @let slice = view();
          <div class="grid-scroll" #scroller (scroll)="onScroll()">
            <table
              #table
              role="grid"
              tabindex="0"
              aria-label="Query results"
              [attr.aria-describedby]="hintId"
              [attr.aria-rowcount]="rows.length + 1"
              [attr.aria-colcount]="columns.length + 1"
              [attr.aria-activedescendant]="activeCellId()"
              [style.width.px]="tableWidth()"
              (keydown)="onKeydown($event)"
              (focus)="onFocus()"
            >
              <colgroup>
                <col [style.width.px]="gutterWidth()" />
                @for (entry of columns; track entry.index) {
                  <col [style.width.px]="widths[entry.index]" />
                }
                <!-- Takes the width the columns do not need, so they keep the widths set above. -->
                <col />
              </colgroup>
              <thead>
                <tr aria-rowindex="1">
                  <th class="gutter" scope="col" aria-colindex="1">
                    <span aria-hidden="true">#</span><span class="visually-hidden">Row</span>
                  </th>
                  @for (entry of columns; track entry.index; let column = $index) {
                    <th
                      scope="col"
                      [id]="cellId(-1, column)"
                      [attr.aria-colindex]="column + 2"
                      [attr.aria-sort]="ariaSort(entry.index)"
                      [class.numeric]="align[entry.index]"
                      [class.sorted]="sort()?.index === entry.index"
                      [class.descending]="
                        sort()?.index === entry.index && sort()?.direction === 'descending'
                      "
                      [class.active]="isActive(-1, column)"
                      [title]="'Sort by ' + entry.column.name"
                      (click)="onHeadingClick(column, entry.index)"
                    >
                      <span class="column-heading"
                        ><span class="type-icon" aria-hidden="true">{{
                          typeIcon(entry.column, entry.index)
                        }}</span>
                        <span class="column-label"
                          ><span class="col-name">{{ entry.column.name }}</span
                          ><span class="col-type">{{ entry.column.dataType }}</span></span
                        >
                        <svg class="sort-mark" viewBox="0 0 10 10" aria-hidden="true">
                          <path d="M2.5 6.25 5 3.75l2.5 2.5" />
                        </svg>
                      </span>
                    </th>
                  }
                  <th class="filler" aria-hidden="true"></th>
                </tr>
              </thead>
              <tbody>
                @if (slice.before > 0) {
                  <tr class="spacer" aria-hidden="true">
                    <td [attr.colspan]="columns.length + 2" [style.height.px]="slice.before"></td>
                  </tr>
                }
                @for (row of slice.rows; track row.index; let offset = $index) {
                  @let position = slice.first + offset;
                  <tr [attr.aria-rowindex]="position + 2">
                    <td class="gutter" role="rowheader" aria-colindex="1">{{ row.index + 1 }}</td>
                    @for (entry of columns; track entry.index; let column = $index) {
                      @let cell = row.values[entry.index];
                      <td
                        class="cell"
                        [id]="cellId(position, column)"
                        [attr.aria-colindex]="column + 2"
                        [class.numeric]="align[entry.index]"
                        [class.null]="cell === null"
                        [class.active]="isActive(position, column)"
                        (click)="activate(position, column)"
                        (dblclick)="copyValue(cell)"
                      >
                        {{ render(cell) }}
                      </td>
                    }
                    <td class="filler" aria-hidden="true"></td>
                  </tr>
                } @empty {
                  <tr>
                    <td class="no-match" [attr.colspan]="columns.length + 2">
                      {{
                        data.rows.length === 0
                          ? 'The query returned no rows.'
                          : 'No returned rows match this search.'
                      }}
                    </td>
                  </tr>
                }
                @if (slice.after > 0) {
                  <tr class="spacer" aria-hidden="true">
                    <td [attr.colspan]="columns.length + 2" [style.height.px]="slice.after"></td>
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
              @if (sort()) {
                <span>Sorted within the returned rows</span>
              }
            }
            <span class="key-hint" [id]="hintId"
              >Arrow keys move between cells. Enter or double-click copies a cell; on a heading,
              Enter sorts.</span
            >
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
        overscroll-behavior: contain;
      }
      .empty {
        padding: 18px;
        color: var(--text-muted);
        font-size: 13px;
      }
      table {
        table-layout: fixed;
        border-collapse: separate;
        border-spacing: 0;
        min-width: 100%;
        font-family: var(--mono);
        font-size: 12px;
      }
      table:focus {
        outline: none;
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
        overflow: hidden;
        vertical-align: bottom;
        cursor: pointer;
        user-select: none;
      }
      thead th:hover {
        background: var(--surface-3);
      }
      thead th.gutter,
      thead th.filler {
        cursor: default;
      }
      thead th.filler:hover {
        background: var(--surface-2);
      }
      th.filler,
      tbody td.filler {
        padding: 0;
      }
      .column-heading {
        display: flex;
        align-items: center;
        gap: 7px;
        min-width: 0;
      }
      .numeric .column-heading {
        justify-content: flex-end;
      }
      .column-label {
        min-width: 0;
        overflow: hidden;
      }
      .type-icon {
        flex: none;
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
        overflow: hidden;
        color: var(--text);
        font-weight: 600;
        text-overflow: ellipsis;
      }
      .col-type {
        display: block;
        color: var(--text-faint);
        font-size: 10px;
        font-weight: 400;
        text-transform: lowercase;
      }
      .sort-mark {
        flex: none;
        width: 10px;
        height: 10px;
        fill: none;
        stroke: currentColor;
        stroke-width: 1.4;
        stroke-linecap: round;
        stroke-linejoin: round;
        color: var(--text-faint);
        opacity: 0;
        transition:
          opacity 0.12s ease,
          transform 0.15s ease;
      }
      thead th:hover .sort-mark {
        opacity: 0.6;
      }
      thead th.sorted .sort-mark {
        opacity: 1;
        color: var(--accent);
      }
      thead th.descending .sort-mark {
        transform: rotate(180deg);
      }
      /* 28px is ROW_HEIGHT; the virtual scroll depends on every body row being exactly this tall. */
      tbody tr {
        height: 28px;
      }
      tbody td {
        box-sizing: border-box;
        height: 28px;
        padding: 0 12px;
        line-height: 27px;
        border-bottom: 1px solid var(--border);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      tbody tr.spacer td {
        height: auto;
        padding: 0;
        border: 0;
      }
      tbody tr:not(.spacer):hover td {
        background: var(--surface-1);
      }
      .cell {
        cursor: default;
      }
      .active {
        box-shadow: inset 0 0 0 1px var(--border-strong);
      }
      table:focus .active {
        box-shadow: inset 0 0 0 2px var(--info);
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
        color: var(--text-faint);
        text-align: right;
        user-select: none;
        background: var(--surface-1);
        position: sticky;
        left: 0;
        border-right: 1px solid var(--border);
      }
      tbody td.gutter {
        padding: 0 10px;
        font-variant-numeric: tabular-nums;
      }
      thead .gutter {
        z-index: 2;
      }
      .no-match {
        padding: 20px;
        color: var(--text-muted);
        text-align: center;
      }
      tbody td.no-match {
        height: auto;
        line-height: 1.5;
        white-space: normal;
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
      .key-hint {
        margin-left: auto;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
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
        .key-hint {
          display: none;
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
  protected readonly sort = signal<SortOrder | null>(null);
  protected readonly active = signal<ActiveCell | null>(null);

  private readonly instance = ++gridInstances;
  protected readonly hintId = `lh-grid-${this.instance}-hint`;
  private readonly scroller = viewChild<ElementRef<HTMLDivElement>>('scroller');
  private readonly table = viewChild<ElementRef<HTMLTableElement>>('table');
  private readonly scrollTop = signal(0);
  private readonly viewport = signal(0);
  private scrollFrame = 0;

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

  /** Each row's rendered text, lower-cased once per result rather than once per keystroke. */
  private readonly searchText = computed(() =>
    (this.result()?.rows ?? []).map((values) =>
      values
        .map((value) => this.render(value))
        .join('\u0000')
        .toLowerCase(),
    ),
  );
  private readonly filteredRows = computed<GridRow[]>(() => {
    const term = this.filter().trim().toLowerCase();
    const text = this.searchText();
    const rows = (this.result()?.rows ?? []).map((values, index) => ({ values, index }));
    return term ? rows.filter(({ index }) => text[index].includes(term)) : rows;
  });

  /** The rows in display order. Nulls sort last in both directions; ties keep response order. */
  protected readonly sortedRows = computed<GridRow[]>(() => {
    const rows = this.filteredRows();
    const order = this.sort();
    if (!order) {
      return rows;
    }

    const numeric = this.alignRight()[order.index] ?? false;
    const factor = order.direction === 'descending' ? -1 : 1;
    return [...rows].sort((left, right) => {
      const a = left.values[order.index];
      const b = right.values[order.index];
      const aNull = a === null || a === undefined;
      const bNull = b === null || b === undefined;
      if (aNull || bNull) {
        return aNull === bNull ? left.index - right.index : aNull ? 1 : -1;
      }
      return compareCells(a, b, numeric) * factor || left.index - right.index;
    });
  });

  /** The slice of rows to render, with the heights of the rows above and below it. */
  protected readonly view = computed(() => {
    const rows = this.sortedRows();
    const total = rows.length;
    const viewport = this.viewport() || FALLBACK_VIEWPORT;
    const first = Math.min(
      Math.max(0, Math.floor(this.scrollTop() / ROW_HEIGHT) - OVERSCAN),
      Math.max(0, total - 1),
    );
    const last = Math.min(total, first + Math.ceil(viewport / ROW_HEIGHT) + 1 + OVERSCAN * 2);
    return {
      first,
      rows: rows.slice(first, last),
      before: first * ROW_HEIGHT,
      after: (total - last) * ROW_HEIGHT,
    };
  });

  /**
   * Column widths, fixed per result.
   *
   * A virtualised table cannot size columns from its content, because the content changes as it
   * scrolls and the columns would jump. Widths come from the heading and a sample of rows instead,
   * capped so one long value cannot push every other column off screen; longer values ellipsise.
   */
  protected readonly columnWidths = computed<number[]>(() => {
    const data = this.result();
    if (!data) {
      return [];
    }

    const charWidth = monoCharWidth();
    const sample = data.rows.slice(0, WIDTH_SAMPLE);
    return data.columns.map((column, index) => {
      const heading = Math.max(column.name.length, column.dataType.length) * charWidth;
      let longest = 0;
      for (const row of sample) {
        longest = Math.max(longest, this.render(row[index]).length);
      }
      const width = Math.max(heading + HEADING_CHROME, longest * charWidth + CELL_PADDING);
      return Math.round(Math.min(MAX_COLUMN_WIDTH, Math.max(MIN_COLUMN_WIDTH, width)));
    });
  });
  protected readonly gutterWidth = computed(() =>
    Math.max(44, Math.round(String(this.result()?.rows.length ?? 0).length * monoCharWidth() + 22)),
  );
  protected readonly tableWidth = computed(() => {
    const widths = this.columnWidths();
    return this.visibleColumns().reduce(
      (total, { index }) => total + (widths[index] ?? MIN_COLUMN_WIDTH),
      this.gutterWidth(),
    );
  });

  /** The active cell's id, only while it is rendered: a reference to a missing id helps nobody. */
  protected readonly activeCellId = computed(() => {
    const cell = this.active();
    if (!cell || cell.column >= this.visibleColumns().length) {
      return null;
    }
    const visible = this.view();
    if (
      cell.row >= 0 &&
      (cell.row < visible.first || cell.row >= visible.first + visible.rows.length)
    ) {
      return null;
    }
    return this.cellId(cell.row, cell.column);
  });

  constructor() {
    // The viewport decides how many rows to render; follow it as the panes are resized.
    effect((onCleanup) => {
      const element = this.scroller()?.nativeElement;
      if (!element) {
        return;
      }
      this.viewport.set(element.clientHeight);
      if (typeof ResizeObserver === 'undefined') {
        return;
      }
      const observer = new ResizeObserver(() => this.viewport.set(element.clientHeight));
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });
  }

  ngOnChanges(): void {
    // Search, column, sort, and cursor choices describe one result shape. Carrying them into the
    // next execution can hide every row or point at the wrong positional column, especially when
    // the query shape changes.
    this.filter.set('');
    this.hiddenColumns.set(new Set<number>());
    this.columnsOpen.set(false);
    this.notice.set(null);
    this.sort.set(null);
    this.active.set(null);
    this.scrollToTop();
  }

  protected search(term: string): void {
    this.filter.set(term);
    this.active.set(null);
    this.scrollToTop();
  }

  protected toggleColumn(index: number): void {
    const next = new Set(this.hiddenColumns());
    if (!next.delete(index)) next.add(index);
    this.hiddenColumns.set(next);
    this.active.set(null);
  }

  protected cellId(row: number, column: number): string {
    return `lh-grid-${this.instance}-${row < 0 ? 'h' : row}-${column}`;
  }

  protected isActive(row: number, column: number): boolean {
    const cell = this.active();
    return cell !== null && cell.row === row && cell.column === column;
  }

  protected ariaSort(index: number): 'ascending' | 'descending' | null {
    const order = this.sort();
    return order?.index === index ? order.direction : null;
  }

  /** Cycles a column through ascending, descending, and the response's own order. */
  protected toggleSort(index: number): void {
    const order = this.sort();
    if (order?.index !== index) {
      this.sort.set({ index, direction: 'ascending' });
    } else if (order.direction === 'ascending') {
      this.sort.set({ index, direction: 'descending' });
    } else {
      this.sort.set(null);
    }
  }

  protected onHeadingClick(column: number, index: number): void {
    this.activate(-1, column);
    this.toggleSort(index);
  }

  protected activate(row: number, column: number): void {
    this.active.set({ row, column });
    this.table()?.nativeElement.focus({ preventScroll: true });
  }

  /** Tabbing into the grid lands on its first cell, so the arrow keys have somewhere to start. */
  protected onFocus(): void {
    if (!this.active()) {
      this.active.set({ row: this.sortedRows().length ? 0 : -1, column: 0 });
    }
  }

  protected onKeydown(event: KeyboardEvent): void {
    const lastColumn = this.visibleColumns().length - 1;
    if (lastColumn < 0) {
      return;
    }

    const lastRow = this.sortedRows().length - 1;
    const firstRow = lastRow >= 0 ? 0 : -1;
    const current = this.active() ?? { row: firstRow, column: 0 };
    const cell = { row: Math.min(current.row, lastRow), column: Math.min(current.column, lastColumn) };
    const modifier = event.ctrlKey || event.metaKey;
    const page = Math.max(1, Math.floor(this.bodyHeight() / ROW_HEIGHT) - 1);

    let next: ActiveCell;
    switch (event.key) {
      case 'ArrowDown':
        next = { ...cell, row: Math.min(cell.row + 1, lastRow) };
        break;
      case 'ArrowUp':
        next = { ...cell, row: Math.max(cell.row - 1, -1) };
        break;
      case 'ArrowRight':
        next = { ...cell, column: Math.min(cell.column + 1, lastColumn) };
        break;
      case 'ArrowLeft':
        next = { ...cell, column: Math.max(cell.column - 1, 0) };
        break;
      case 'Home':
        next = modifier ? { row: firstRow, column: 0 } : { ...cell, column: 0 };
        break;
      case 'End':
        next = modifier ? { row: lastRow, column: lastColumn } : { ...cell, column: lastColumn };
        break;
      case 'PageDown':
        next = { ...cell, row: Math.min(cell.row + page, lastRow) };
        break;
      case 'PageUp':
        next = { ...cell, row: Math.max(cell.row - page, firstRow) };
        break;
      case 'Enter':
      case ' ':
        // ⌘⏎ is the workbench's Run shortcut; reading it as Enter would copy a cell instead.
        if (modifier || event.altKey || event.shiftKey) {
          return;
        }
        event.preventDefault();
        if (cell.row < 0) {
          this.toggleSort(this.visibleColumns()[cell.column].index);
        } else if (event.key === 'Enter') {
          void this.copyCell(cell);
        }
        return;
      case 'c':
      case 'C':
        // Leave ⌘C alone when the operator has selected text themselves.
        if (modifier && cell.row >= 0 && !hasTextSelection()) {
          event.preventDefault();
          void this.copyCell(cell);
        }
        return;
      default:
        return;
    }

    event.preventDefault();
    this.active.set(next);
    this.reveal(next);
  }

  protected onScroll(): void {
    const element = this.scroller()?.nativeElement;
    if (!element) {
      return;
    }
    if (typeof requestAnimationFrame === 'undefined') {
      this.scrollTop.set(element.scrollTop);
      return;
    }
    if (!this.scrollFrame) {
      this.scrollFrame = requestAnimationFrame(() => {
        this.scrollFrame = 0;
        this.scrollTop.set(element.scrollTop);
      });
    }
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
      ...this.sortedRows().map(({ values }) =>
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
      ...this.sortedRows().map(({ values }) =>
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

  private async copyCell(cell: ActiveCell): Promise<void> {
    const row = this.sortedRows()[cell.row];
    const column = this.visibleColumns()[cell.column];
    if (row && column) {
      await this.copyValue(row.values[column.index]);
    }
  }

  /** The height available to body rows: the viewport less the sticky heading. */
  private bodyHeight(): number {
    const heading = this.table()?.nativeElement.tHead?.offsetHeight ?? ROW_HEIGHT;
    return Math.max(ROW_HEIGHT, (this.viewport() || FALLBACK_VIEWPORT) - heading);
  }

  /** Scrolls just enough to bring a cell into view below the sticky heading and beside the gutter. */
  private reveal(cell: ActiveCell): void {
    const scroller = this.scroller()?.nativeElement;
    if (!scroller) {
      return;
    }

    if (cell.row >= 0) {
      const top = cell.row * ROW_HEIGHT;
      const height = this.bodyHeight();
      let scrollTop = scroller.scrollTop;
      if (top < scrollTop) {
        scrollTop = top;
      } else if (top + ROW_HEIGHT > scrollTop + height) {
        scrollTop = top + ROW_HEIGHT - height;
      }
      if (scrollTop !== scroller.scrollTop) {
        scroller.scrollTop = scrollTop;
        this.scrollTop.set(scrollTop);
      }
    }

    // Horizontal position needs the cell rendered, which happens after this change is applied.
    const align = (): void => {
      const element = scroller.querySelector<HTMLElement>(`#${this.cellId(cell.row, cell.column)}`);
      if (!element) {
        return;
      }
      const gutter = this.gutterWidth();
      const left = element.offsetLeft - gutter;
      const right = element.offsetLeft + element.offsetWidth;
      if (left < scroller.scrollLeft) {
        scroller.scrollLeft = left;
      } else if (right > scroller.scrollLeft + scroller.clientWidth) {
        scroller.scrollLeft = right - scroller.clientWidth;
      }
    };
    if (typeof requestAnimationFrame === 'undefined') {
      align();
    } else {
      requestAnimationFrame(align);
    }
  }

  private scrollToTop(): void {
    this.scrollTop.set(0);
    const element = this.scroller()?.nativeElement;
    if (element) {
      element.scrollTop = 0;
      element.scrollLeft = 0;
    }
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

const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

/**
 * Orders two non-null cells.
 *
 * Integers beyond JavaScript's safe range arrive as strings so they survive transport, which
 * means a numeric column can mix numbers and digit strings. Those compare as BigInt where both
 * are whole; anything else numeric compares as a double, and text collates naturally.
 */
function compareCells(a: unknown, b: unknown, numeric: boolean): number {
  if (numeric) {
    const whole = /^-?\d+$/;
    const aText = String(a);
    const bText = String(b);
    if (whole.test(aText) && whole.test(bText)) {
      const difference = BigInt(aText) - BigInt(bText);
      return difference === 0n ? 0 : difference < 0n ? -1 : 1;
    }
    const difference = Number(a) - Number(b);
    if (!Number.isNaN(difference)) {
      return Math.sign(difference);
    }
  }
  if (typeof a === 'boolean' && typeof b === 'boolean') {
    return Number(a) - Number(b);
  }
  const aText = typeof a === 'object' ? JSON.stringify(a) : String(a);
  const bText = typeof b === 'object' ? JSON.stringify(b) : String(b);
  return collator.compare(aText, bText);
}

function hasTextSelection(): boolean {
  return typeof window !== 'undefined' && (window.getSelection()?.toString().length ?? 0) > 0;
}

let cachedCharWidth: number | null = null;

/** The advance of one character in the grid's 12px monospace face, measured once per page. */
function monoCharWidth(): number {
  if (cachedCharWidth !== null) {
    return cachedCharWidth;
  }

  let width = 0;
  if (typeof document !== 'undefined' && document.body) {
    const probe = document.createElement('span');
    probe.style.cssText =
      'position:absolute;visibility:hidden;white-space:pre;font:12px var(--mono);';
    probe.textContent = '0'.repeat(20);
    document.body.appendChild(probe);
    width = probe.getBoundingClientRect().width / 20;
    probe.remove();
  }
  // Layout-less environments report nothing; 0.6em is the common monospace advance.
  cachedCharWidth = width > 0 ? width : 7.2;
  return cachedCharWidth;
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
