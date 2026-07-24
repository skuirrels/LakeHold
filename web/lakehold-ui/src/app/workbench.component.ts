import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from './auth.service';
import { CatalogExplorerComponent } from './catalog-explorer.component';
import { LakehouseService } from './lakehouse.service';
import { MaintenanceOperation, QueryResponse, QueryRun, Schema, Snapshot, Tenant } from './models';
import { ResultGridComponent } from './result-grid.component';

const STARTER_SQL = `-- Aggregate 250k rows in a few milliseconds.
SELECT
    country,
    count(*)                AS purchases,
    ROUND(sum(revenue), 2)  AS revenue
FROM events
WHERE event_type = 'purchase'
GROUP BY country
ORDER BY revenue DESC;`;

type BottomTab = 'results' | 'history' | 'snapshots';

/** The SQL IDE: catalog explorer, editor, results, history, and catalog maintenance. */
@Component({
  selector: 'lh-workbench',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CatalogExplorerComponent, ResultGridComponent, RouterLink],
  templateUrl: './workbench.component.html',
  styleUrl: './workbench.component.css',
})
export class WorkbenchComponent {
  private readonly api = inject(LakehouseService);
  protected readonly auth = inject(AuthService);

  /** Whether the credential popover is open, and the token being typed into it. */
  protected readonly credentialOpen = signal(false);
  protected readonly tokenDraft = signal('');

  protected readonly tenants = signal<Tenant[]>([]);
  protected readonly tenantSlug = signal<string | null>(null);
  protected readonly catalogName = signal<string | null>(null);

  protected readonly schemas = signal<Schema[]>([]);
  protected readonly schemasLoading = signal(false);

  protected readonly sql = signal(STARTER_SQL);
  protected readonly running = signal(false);
  protected readonly result = signal<QueryResponse | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);

  protected readonly history = signal<QueryRun[]>([]);
  /** A destructive operation whose dry run has completed and is awaiting confirmation. */
  protected readonly pendingApply = signal<MaintenanceOperation | null>(null);
  protected readonly snapshots = signal<Snapshot[]>([]);
  protected readonly tab = signal<BottomTab>('results');

  protected readonly catalogs = computed(
    () => this.tenants().find((t) => t.slug === this.tenantSlug())?.catalogs ?? [],
  );

  protected readonly ready = computed(() => this.tenantSlug() !== null && this.catalogName() !== null);

  protected readonly summary = computed(() => {
    const data = this.result();
    if (!data) {
      return null;
    }

    const time = `${data.elapsedMilliseconds.toFixed(1)} ms`;

    // A statement that changed rows reports what it changed. Reporting "0 rows" for a successful
    // insert is what the affected-row count exists to stop, so it takes precedence over the
    // returned-row count, which is zero by definition here.
    if (data.rowsAffected !== null && data.rowsAffected !== undefined) {
      const affected = `${data.rowsAffected} row${data.rowsAffected === 1 ? '' : 's'} affected`;
      return `${affected} · ${time}`;
    }

    const rows = `${data.rows.length} row${data.rows.length === 1 ? '' : 's'}`;
    return data.truncated ? `${rows} (truncated) · ${time}` : `${rows} · ${time}`;
  });

  constructor() {
    this.loadTenants();
  }

  /** Loads the tenants the current credential can see, selecting the first as a starting point. */
  private loadTenants(): void {
    this.api.listTenants().subscribe({
      next: (tenants) => {
        this.tenants.set(tenants);
        this.error.set(null);
        const first = tenants[0];
        if (first) {
          this.tenantSlug.set(first.slug);
          this.catalogName.set(first.catalogs[0]?.name ?? null);
          this.refreshCatalog();
          this.refreshHistory();
        } else {
          this.tenantSlug.set(null);
          this.catalogName.set(null);
          this.schemas.set([]);
          this.history.set([]);
        }
      },
      error: (err: Error) => this.error.set(err.message),
    });
  }

  /** Toggles the credential popover, seeding the draft with nothing (the token is never echoed back). */
  protected toggleCredential(): void {
    this.tokenDraft.set('');
    this.credentialOpen.update((open) => !open);
  }

  /** Stores the typed token and reloads, since it may change which tenants are visible. */
  protected saveCredential(): void {
    this.auth.setToken(this.tokenDraft());
    this.tokenDraft.set('');
    this.credentialOpen.set(false);
    this.loadTenants();
  }

  /** Forgets the token and reloads as an anonymous caller. */
  protected clearCredential(): void {
    this.auth.clear();
    this.tokenDraft.set('');
    this.credentialOpen.set(false);
    this.loadTenants();
  }

  protected selectTenant(slug: string): void {
    this.tenantSlug.set(slug);
    this.catalogName.set(this.catalogs()[0]?.name ?? null);
    this.refreshCatalog();
    this.refreshHistory();
  }

  protected selectCatalog(name: string): void {
    this.catalogName.set(name);
    this.refreshCatalog();
  }

  protected run(): void {
    const tenant = this.tenantSlug();
    const catalog = this.catalogName();
    const sql = this.sql().trim();

    if (!tenant || !catalog || !sql || this.running()) {
      return;
    }

    this.running.set(true);
    this.error.set(null);
    this.notice.set(null);
    this.tab.set('results');

    this.api.execute(tenant, catalog, sql).subscribe({
      next: (response) => {
        this.result.set(response);
        this.running.set(false);
        this.refreshHistory();

        // DDL can change the object tree, so keep the explorer in step with the catalog.
        if (/^\s*(create|drop|alter)\b/i.test(sql)) {
          this.refreshCatalog();
        }
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.result.set(null);
        this.running.set(false);
        this.refreshHistory();
      },
    });
  }

  /** Cmd/Ctrl+Enter runs; Tab inserts an indent instead of leaving the editor. */
  protected onEditorKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) {
      event.preventDefault();
      this.run();
      return;
    }

    if (event.key === 'Tab') {
      event.preventDefault();
      const target = event.target as HTMLTextAreaElement;
      const { selectionStart, selectionEnd, value } = target;
      target.value = `${value.slice(0, selectionStart)}    ${value.slice(selectionEnd)}`;
      target.selectionStart = target.selectionEnd = selectionStart + 4;
      this.sql.set(target.value);
    }
  }

  protected insertSql(snippet: string): void {
    this.sql.set(snippet);
  }

  /**
   * Populates the editor with a statement that restores a table to a chosen snapshot, ready to run.
   *
   * A DuckLake snapshot is catalog-wide, but a restore is expressed per table:
   * `CREATE OR REPLACE TABLE t AS SELECT * FROM t AT (VERSION => n)` rewrites the table to its rows
   * at that snapshot. It is a single statement — so it runs through the same path as any other query
   * — and it records a *new* snapshot rather than rewriting history, which keeps time travel intact
   * and makes the restore itself reversible. The naive `DELETE` + `INSERT ... AT` reads the snapshot
   * through the pending delete and silently restores the wrong rows, so this form is used instead.
   *
   * The target defaults to the catalog's first table so Run works immediately; other tables are
   * listed as a hint because we cannot know which one the operator means.
   */
  protected restoreSnapshot(snapshot: Snapshot): void {
    const version = snapshot.snapshotId;
    const tables = this.schemas().flatMap((schema) =>
      schema.tables.filter((table) => table.kind !== 'VIEW').map((table) => `${schema.name}.${table.name}`),
    );
    const target = tables[0] ?? 'schema.table';
    const others = tables.length > 1 ? `\n-- Other tables in this catalog: ${tables.slice(1).join(', ')}` : '';

    this.sql.set(
      `-- Restore a table to snapshot ${version} and record a new, reversible snapshot.
-- A snapshot spans the whole catalog; this restores one table. Constraints or defaults
-- added since the snapshot are not carried over. Review the target, then press Run.${others}
CREATE OR REPLACE TABLE ${target} AS
SELECT * FROM ${target} AT (VERSION => ${version});`,
    );
  }

  /**
   * Runs a maintenance operation.
   *
   * `expire` and `cleanup` run as a dry run first. The result is shown with a confirmation
   * affordance, and nothing is destroyed until the operator explicitly applies it — snapshot
   * expiry and file cleanup are both unrecoverable.
   */
  protected runMaintenance(operation: MaintenanceOperation, apply = false): void {
    const tenant = this.tenantSlug();
    const catalog = this.catalogName();
    if (!tenant || !catalog) {
      return;
    }

    this.notice.set(null);
    this.error.set(null);
    this.pendingApply.set(null);

    this.api.runMaintenance(tenant, catalog, operation, apply).subscribe({
      next: (res) => {
        this.notice.set(`${res.operation}: ${res.detail} (${res.elapsedMilliseconds.toFixed(0)} ms)`);
        this.pendingApply.set(res.dryRun ? operation : null);

        // Expiry and cleanup change which snapshots exist, so the panel would otherwise go stale.
        if (!res.dryRun && this.tab() === 'snapshots') {
          this.refreshSnapshots();
        }
      },
      error: (err: Error) => this.error.set(err.message),
    });
  }

  protected confirmApply(): void {
    const operation = this.pendingApply();
    if (operation) {
      this.runMaintenance(operation, true);
    }
  }

  protected cancelApply(): void {
    this.pendingApply.set(null);
    this.notice.set(null);
  }

  protected showTab(tab: BottomTab): void {
    this.tab.set(tab);
    if (tab === 'snapshots') {
      this.refreshSnapshots();
    }
  }

  protected replay(run: QueryRun): void {
    this.sql.set(run.sql);
    this.tab.set('results');
  }

  protected formatTime(iso: string): string {
    return new Date(iso).toLocaleTimeString();
  }

  private refreshCatalog(): void {
    const tenant = this.tenantSlug();
    const catalog = this.catalogName();
    if (!tenant || !catalog) {
      this.schemas.set([]);
      return;
    }

    this.schemasLoading.set(true);
    this.api.getSchemas(tenant, catalog).subscribe({
      next: (schemas) => {
        this.schemas.set(schemas);
        this.schemasLoading.set(false);
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.schemasLoading.set(false);
      },
    });
  }

  private refreshHistory(): void {
    const tenant = this.tenantSlug();
    if (!tenant) {
      return;
    }

    // History is advisory; a failure here must not replace the query error the user is reading.
    this.api.getHistory(tenant).subscribe({ next: (runs) => this.history.set(runs), error: () => undefined });
  }

  private refreshSnapshots(): void {
    const tenant = this.tenantSlug();
    const catalog = this.catalogName();
    if (!tenant || !catalog) {
      return;
    }

    this.api.getSnapshots(tenant, catalog).subscribe({
      next: (snapshots) => this.snapshots.set(snapshots),
      error: (err: Error) => this.error.set(err.message),
    });
  }
}
