import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { Schema, SchemaTable } from './models';

/** Tree of schemas, tables, and columns for the current catalog. */
@Component({
  selector: 'lh-catalog-explorer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="explorer">
      <input
        class="filter"
        type="search"
        placeholder="Filter tables and columns…"
        [value]="filter()"
        (input)="filter.set($any($event.target).value)"
        aria-label="Filter catalog objects" />

      @if (loading()) {
        <p class="hint">Loading catalog…</p>
      } @else if (visible().length === 0) {
        <p class="hint">{{ filter() ? 'No objects match the filter.' : 'This catalog has no tables yet.' }}</p>
      }

      @for (schema of visible(); track schema.name) {
        <div class="schema">
          <div class="schema-name">{{ schema.name }}</div>

          @for (table of schema.tables; track table.name) {
            <div class="table">
              <!-- Two sibling buttons rather than one nested inside the other: a button inside a
                   button is invalid HTML and browsers recover from it inconsistently. -->
              <div class="table-row">
                <button
                  class="table-toggle"
                  type="button"
                  [attr.aria-expanded]="isOpen(schema.name + '.' + table.name)"
                  (click)="toggle(schema.name + '.' + table.name)">
                  <span class="chevron" [class.open]="isOpen(schema.name + '.' + table.name)">▸</span>
                  <span class="table-name">{{ table.name }}</span>
                  <span class="table-kind">{{ table.kind === 'VIEW' ? 'view' : '' }}</span>
                </button>
                <button
                  class="insert"
                  type="button"
                  [attr.aria-label]="'Insert a SELECT for ' + table.name"
                  title="Insert into editor"
                  (click)="insertSelect(schema.name, table)">
                  +
                </button>
              </div>

              @if (isOpen(schema.name + '.' + table.name)) {
                <ul class="columns">
                  @for (column of table.columns; track column.name) {
                    <li>
                      <span class="col-name">{{ column.name }}</span>
                      <span class="col-type">{{ column.dataType }}</span>
                    </li>
                  }
                </ul>
              }
            </div>
          }
        </div>
      }
    </div>
  `,
  styles: [
    `
      .explorer {
        display: flex;
        flex-direction: column;
        gap: 2px;
        padding: 10px;
        overflow-y: auto;
        height: 100%;
      }

      .filter {
        width: 100%;
        padding: 6px 9px;
        margin-bottom: 8px;
        border-radius: var(--radius-sm);
        border: 1px solid var(--border);
        background: var(--surface-0);
        color: var(--text);
        font-size: 12px;
      }

      .filter::placeholder {
        color: var(--text-faint);
      }

      .hint {
        color: var(--text-faint);
        font-size: 12px;
        padding: 4px 2px;
        margin: 0;
      }

      .schema-name {
        font-size: 10px;
        font-weight: 700;
        letter-spacing: 0.09em;
        text-transform: uppercase;
        color: var(--text-faint);
        padding: 10px 2px 4px;
      }

      .table-row {
        display: flex;
        align-items: center;
        gap: 2px;
        width: 100%;
        border-radius: var(--radius-sm);
      }

      .table-row:hover {
        background: var(--surface-2);
      }

      .table-toggle {
        display: flex;
        align-items: center;
        gap: 6px;
        flex: 1;
        min-width: 0;
        padding: 4px 6px;
        text-align: left;
        font-size: 13px;
      }

      .chevron {
        color: var(--text-faint);
        font-size: 10px;
        transition: transform 0.12s ease;
        display: inline-block;
      }

      .chevron.open {
        transform: rotate(90deg);
      }

      .table-name {
        font-family: var(--mono);
        font-size: 12px;
        flex: 1;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .table-kind {
        font-size: 10px;
        color: var(--text-faint);
        font-style: italic;
      }

      .insert {
        opacity: 0;
        color: var(--text-faint);
        font-size: 15px;
        line-height: 1;
        padding: 4px 8px;
        flex-shrink: 0;
      }

      .table-row:hover .insert,
      .insert:focus-visible {
        opacity: 1;
      }

      .insert:hover {
        color: var(--accent);
      }

      .columns {
        list-style: none;
        margin: 0;
        padding: 2px 0 4px 22px;
      }

      .columns li {
        display: flex;
        justify-content: space-between;
        gap: 10px;
        padding: 2px 6px;
        font-family: var(--mono);
        font-size: 11px;
      }

      .col-name {
        color: var(--text-muted);
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .col-type {
        color: var(--text-faint);
        flex-shrink: 0;
      }
    `,
  ],
})
export class CatalogExplorerComponent {
  readonly schemas = input.required<Schema[]>();
  readonly loading = input(false);

  /** Emits a `SELECT` for the chosen table. */
  readonly insertSql = output<string>();

  protected readonly filter = signal('');
  private readonly open = signal(new Set<string>());

  /**
   * A lowercased search index over the current catalog, rebuilt only when the schema tree changes.
   *
   * The filter runs on every keystroke, so lowercasing every table and column name inside it made
   * each keystroke cost O(tables × columns) string allocations — wrong for the wide catalogs this
   * targets. Precomputing the lowercased forms here moves that cost off the keystroke path; the
   * filter then only scans the prepared strings.
   */
  private readonly searchIndex = computed(() =>
    this.schemas().map((schema) => ({
      schema,
      tables: schema.tables.map((table) => ({
        table,
        nameLower: table.name.toLowerCase(),
        columnsLower: table.columns.map((column) => column.name.toLowerCase()),
      })),
    })),
  );

  /**
   * Schemas narrowed by the filter.
   *
   * A table matches on its own name or on any column name, so searching for a column finds the
   * table that holds it — the common case when you know the field but not where it lives.
   */
  protected readonly visible = computed<Schema[]>(() => {
    const term = this.filter().trim().toLowerCase();
    if (!term) {
      return this.schemas();
    }

    const narrowed: Schema[] = [];
    for (const entry of this.searchIndex()) {
      const tables = entry.tables
        .filter((t) => t.nameLower.includes(term) || t.columnsLower.some((c) => c.includes(term)))
        .map((t) => t.table);

      if (tables.length > 0) {
        narrowed.push({ ...entry.schema, tables });
      }
    }

    return narrowed;
  });

  protected isOpen(key: string): boolean {
    return this.open().has(key);
  }

  protected toggle(key: string): void {
    const next = new Set(this.open());
    if (!next.delete(key)) {
      next.add(key);
    }

    this.open.set(next);
  }

  protected insertSelect(schema: string, table: SchemaTable): void {
    this.insertSql.emit(`SELECT *\nFROM ${schema}.${table.name}\nLIMIT 100;`);
  }
}
