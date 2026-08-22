import {
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  effect,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { DataConnector, QueryRun, SavedQuery, Schema, TableReference, Tenant } from './models';
import { WorkbenchDestination } from './workbench-navigation.component';
import { WorkbenchQuerySource } from './workbench-query-source';

type SearchAction =
  | { type: 'navigate'; destination: WorkbenchDestination }
  | { type: 'table'; table: TableReference }
  | { type: 'query'; source: WorkbenchQuerySource }
  | { type: 'context'; tenant: string; catalog: string };

interface SearchItem {
  key: string;
  kind: string;
  title: string;
  detail: string;
  terms: string;
  action: SearchAction;
}

/** Keyboard-first search across the selected Workbench context and its main commands. */
@Component({
  selector: 'lh-workbench-search',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown)': 'handleDocumentKeydown($event)' },
  template: `
    @if (open()) {
      <button
        class="search-backdrop"
        type="button"
        tabindex="-1"
        aria-hidden="true"
        aria-label="Close search"
        (click)="dismiss()"
      ></button>
      <section
        class="search-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="workbench-search-title"
      >
        <h2 id="workbench-search-title" class="visually-hidden">Search or jump to</h2>
        <div class="search-box">
          <span aria-hidden="true">⌕</span>
          <input
            #searchInput
            type="search"
            aria-label="Search or jump to"
            placeholder="Search tables, columns, queries, connectors, history or commands…"
            [value]="query()"
            (input)="query.set($any($event.target).value)"
            (keydown.enter)="activateFirst()"
          />
          <kbd>Esc</kbd>
        </div>
        <div class="search-results">
          @for (item of results(); track item.key) {
            <button type="button" class="search-result" (click)="activate(item.action)">
              <span class="result-kind">{{ item.kind }}</span>
              <span
                ><strong>{{ item.title }}</strong
                ><small>{{ item.detail }}</small></span
              >
            </button>
          } @empty {
            <p class="empty">No tables, queries, connectors, history or commands match.</p>
          }
        </div>
        <footer>
          <span>Search is scoped to metadata you can already access.</span
          ><span><kbd>↵</kbd> open · <kbd>⌘K</kbd> toggle</span>
        </footer>
      </section>
    }
  `,
  styleUrl: './workbench-search.component.css',
})
export class WorkbenchSearchComponent {
  private readonly searchInput = viewChild<ElementRef<HTMLInputElement>>('searchInput');
  private previouslyFocused: HTMLElement | null = null;
  private wasOpen = false;
  readonly open = input(false);
  readonly tenants = input<readonly Tenant[]>([]);
  readonly schemas = input<readonly Schema[]>([]);
  readonly queries = input<readonly SavedQuery[]>([]);
  readonly connectors = input<readonly DataConnector[]>([]);
  readonly history = input<readonly QueryRun[]>([]);
  readonly canManageConnectors = input(false);
  readonly close = output<void>();
  readonly shortcut = output<void>();
  readonly navigate = output<WorkbenchDestination>();
  readonly inspectTable = output<TableReference>();
  readonly openSource = output<WorkbenchQuerySource>();
  readonly selectContext = output<{ tenant: string; catalog: string }>();
  protected readonly query = signal('');

  constructor() {
    effect(() => {
      const open = this.open();
      const input = this.searchInput();
      if (open) {
        if (!this.wasOpen && typeof document !== 'undefined') {
          this.previouslyFocused = document.activeElement as HTMLElement | null;
        }
        this.wasOpen = true;
        input?.nativeElement.focus();
      } else {
        this.query.set('');
        if (this.wasOpen) {
          this.previouslyFocused?.focus();
          this.previouslyFocused = null;
        }
        this.wasOpen = false;
      }
    });
  }

  private readonly items = computed<SearchItem[]>(() => {
    const commands: SearchItem[] = [
      command('New query', 'Open the SQL or LINQ editor', 'workbench'),
      command('Add data', 'Import a file or configure a managed source', 'add-data'),
      command('Catalog', 'Browse tables and columns', 'catalog'),
      command('Query library', 'Find and manage reusable queries', 'queries'),
      command('Query history', 'Review statements and their outcomes', 'history'),
      command('Data history', 'Inspect snapshots and restore table data', 'snapshots'),
      command('Storage', 'Inspect files, partitions and maintenance advice', 'storage'),
      command('Changes', 'Read change data and subscriptions', 'changes'),
      command('Backups', 'Review and restore backup generations', 'backups'),
      command('Eject', 'Create and verify open-format exit bundles', 'ejects'),
      command('Schedule', 'Review maintenance schedules and recent runs', 'schedule'),
    ];
    if (this.canManageConnectors())
      commands.push(command('Connectors', 'Administer governed data sources', 'connectors'));
    const contexts = this.tenants().flatMap((tenant) =>
      tenant.catalogs.map((catalog) => ({
        key: `catalog:${tenant.slug}:${catalog.name}`,
        kind: 'Catalog',
        title: catalog.name,
        detail: tenant.displayName,
        terms: `${tenant.slug} ${tenant.displayName} ${catalog.name}`,
        action: { type: 'context', tenant: tenant.slug, catalog: catalog.name } as SearchAction,
      })),
    );
    const tables = this.schemas().flatMap((schema) =>
      schema.tables.map((table) => ({
        key: `table:${schema.name}:${table.name}`,
        kind: table.kind === 'VIEW' ? 'View' : 'Table',
        title: `${schema.name}.${table.name}`,
        detail: table.columns
          .map((column) => column.name)
          .slice(0, 6)
          .join(', '),
        terms: `${schema.name} ${table.name} ${table.kind} ${table.columns.map((column) => `${column.name} ${column.dataType}`).join(' ')}`,
        action: {
          type: 'table',
          table: { schemaName: schema.name, tableName: table.name },
        } as SearchAction,
      })),
    );
    const queries = this.queries().map((query) => ({
      key: `query:${query.id}`,
      kind: 'Query',
      title: query.name,
      detail: `${query.language ?? 'sql'} · modified ${new Date(query.updatedUtc).toLocaleDateString()}`,
      terms: `${query.name} ${query.description ?? ''} ${query.language ?? 'sql'} ${query.publishedViewName ?? ''}`,
      action: {
        type: 'query',
        source: { language: query.language ?? 'sql', source: query.sql },
      } as SearchAction,
    }));
    const connectors = this.connectors().map((connector) => ({
      key: `connector:${connector.id}`,
      kind: 'Connector',
      title: connector.name,
      detail: `${connector.kind} → ${connector.targetSchema}.${connector.targetTable}`,
      terms: `${connector.name} ${connector.kind} ${connector.owner} ${connector.tags.join(' ')} ${connector.targetSchema} ${connector.targetTable}`,
      action: { type: 'navigate', destination: 'connectors' } as SearchAction,
    }));
    const history = this.history()
      .slice(0, 30)
      .map((run) => ({
        key: `history:${run.id}`,
        kind: 'History',
        title: run.sql.split('\n')[0].slice(0, 90),
        detail: `${run.actorName ?? 'Unknown actor'} · ${run.elapsedMilliseconds.toFixed(0)} ms · ${run.succeeded ? 'succeeded' : 'failed'}`,
        terms: `${run.sql} ${run.actorName ?? ''} ${run.origin} ${run.language ?? 'sql'}`,
        action: {
          type: 'query',
          source: { language: run.language ?? 'sql', source: run.sql },
        } as SearchAction,
      }));
    return [...commands, ...contexts, ...tables, ...queries, ...connectors, ...history];
  });

  protected readonly results = computed(() => {
    const terms = this.query().trim().toLowerCase().split(/\s+/).filter(Boolean);
    const items = this.items();
    return terms.length === 0
      ? items.slice(0, 12)
      : items
          .filter((item) => {
            const haystack =
              `${item.kind} ${item.title} ${item.detail} ${item.terms}`.toLowerCase();
            return terms.every((term) => haystack.includes(term));
          })
          .slice(0, 30);
  });

  protected activateFirst(): void {
    const first = this.results()[0];
    if (first) this.activate(first.action);
  }
  protected activate(action: SearchAction): void {
    if (action.type === 'navigate') this.navigate.emit(action.destination);
    else if (action.type === 'table') this.inspectTable.emit(action.table);
    else if (action.type === 'query') this.openSource.emit(action.source);
    else this.selectContext.emit({ tenant: action.tenant, catalog: action.catalog });
    this.query.set('');
    this.close.emit();
  }
  protected dismiss(): void {
    this.query.set('');
    this.close.emit();
  }
  protected handleDocumentKeydown(event: KeyboardEvent): void {
    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      this.shortcut.emit();
    } else if (this.open() && event.key === 'Escape') {
      event.preventDefault();
      this.dismiss();
    }
  }
}

function command(title: string, detail: string, destination: WorkbenchDestination): SearchItem {
  return {
    key: `command:${destination}`,
    kind: 'Command',
    title,
    detail,
    terms: `${title} ${detail}`,
    action: { type: 'navigate', destination },
  };
}
