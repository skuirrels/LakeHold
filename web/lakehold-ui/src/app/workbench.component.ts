import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { catchError, switchMap } from 'rxjs/operators';
import { AuthService } from './auth.service';
import { BackupsPanelComponent } from './backups-panel.component';
import { BrandMarkComponent } from './brand-mark.component';
import { CatalogExplorerComponent } from './catalog-explorer.component';
import { ChangesPanelComponent } from './changes-panel.component';
import { EjectPanelComponent } from './eject-panel.component';
import { FirstRunComponent, FirstRunMode, WorkspaceRequest } from './first-run.component';
import { formatTime } from './format';
import { ApiError, LakehouseService } from './lakehouse.service';
import {
  AccessContext,
  MaintenanceOperation,
  QueryResponse,
  QueryRun,
  Schema,
  Snapshot,
  Tenant,
} from './models';
import { ResultGridComponent } from './result-grid.component';
import { SchedulePanelComponent } from './schedule-panel.component';
import { StoragePanelComponent } from './storage-panel.component';

const STARTER_SQL = `-- Aggregate 250k rows in a few milliseconds.
SELECT
    country,
    count(*)                AS purchases,
    ROUND(sum(revenue), 2)  AS revenue
FROM events
WHERE event_type = 'purchase'
GROUP BY country
ORDER BY revenue DESC;`;

type BottomTab =
  'results' | 'history' | 'snapshots' | 'storage' | 'backups' | 'ejects' | 'changes' | 'schedule';

/**
 * The SQL IDE.
 *
 * This component owns the chrome — workspace and catalog selectors, the maintenance buttons, the
 * credential popover, the editor, and the tab strip — plus the three panels that belong to running a
 * statement: results, history, and snapshots. Everything else below the editor is its own component,
 * one per tab, each owning its own state, its own requests, and its own error banner.
 *
 * That split is what keeps a failure from leaking between panels: a panel's banner is destroyed with
 * the panel, so a restore refusal cannot hang over the eject list. See `docs/UI.md`.
 */
@Component({
  selector: 'lh-workbench',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    BackupsPanelComponent,
    BrandMarkComponent,
    CatalogExplorerComponent,
    ChangesPanelComponent,
    EjectPanelComponent,
    FirstRunComponent,
    ResultGridComponent,
    RouterLink,
    SchedulePanelComponent,
    StoragePanelComponent,
  ],
  templateUrl: './workbench.component.html',
  styleUrl: './workbench.component.css',
})
export class WorkbenchComponent {
  private readonly api = inject(LakehouseService);
  protected readonly auth = inject(AuthService);

  /**
   * The panels a committed maintenance operation invalidates.
   *
   * Only the visible one exists — the tab strip is a `@switch` — so these are undefined most of the
   * time, which is exactly right: a panel that is not on screen reloads when it is next shown.
   */
  private readonly storagePanel = viewChild(StoragePanelComponent);
  private readonly backupsPanel = viewChild(BackupsPanelComponent);

  /** Whether the credential popover is open, and the token being typed into it. */
  protected readonly credentialOpen = signal(false);
  protected readonly tokenDraft = signal('');

  protected readonly tenants = signal<Tenant[]>([]);
  protected readonly access = signal<AccessContext | null>(null);
  protected readonly tenantSlug = signal<string | null>(null);
  protected readonly catalogName = signal<string | null>(null);

  protected readonly schemas = signal<Schema[]>([]);
  protected readonly schemasLoading = signal(false);

  protected readonly sql = signal(STARTER_SQL);
  protected readonly running = signal(false);
  protected readonly result = signal<QueryResponse | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly errorTitle = signal('Query failed');
  protected readonly notice = signal<string | null>(null);

  protected readonly history = signal<QueryRun[]>([]);
  /** A destructive operation whose dry run has completed and is awaiting confirmation. */
  protected readonly pendingApply = signal<MaintenanceOperation | null>(null);
  protected readonly snapshots = signal<Snapshot[]>([]);
  protected readonly tab = signal<BottomTab>('results');

  /**
   * First-run state, and the reason the workbench is not a SQL IDE yet.
   *
   * `unauthorized` means the deployment requires a credential this browser does not have — the
   * normal state of a production node the moment it starts. `setup` means the credential works and
   * there is genuinely nothing to query: no workspace has ever been created. Distinguishing them
   * matters because the remedy differs, and because an empty picker with no explanation is the
   * worst version of both.
   */
  protected readonly firstRun = signal<FirstRunMode>('none');

  /**
   * Whether the credential this browser holds was the thing refused.
   *
   * A first visit and a rejected token both end in the same panel, and saying nothing would leave
   * an operator retyping a token the server has already refused — expired, revoked, or minted by a
   * node whose database has since been replaced.
   */
  protected readonly signInRejected = signal(false);

  /** The workspace being provisioned, kept for the sentence above the token it is issued. */
  protected readonly setupSlug = signal('');
  protected readonly setupBusy = signal(false);
  protected readonly setupError = signal<string | null>(null);

  /**
   * The token minted for the new workspace, held only long enough to show it once.
   *
   * The server keeps a hash, so this is the only moment it can be copied. It is deliberately not
   * put in `AuthService` until the user has seen it — storing it silently would mean the workbench
   * worked and the operator never learned the credential their scripts and BI tools need.
   */
  protected readonly issuedToken = signal<string | null>(null);

  protected readonly formatTime = formatTime;

  protected readonly catalogs = computed(
    () => this.tenants().find((t) => t.slug === this.tenantSlug())?.catalogs ?? [],
  );

  protected readonly ready = computed(
    () => this.tenantSlug() !== null && this.catalogName() !== null,
  );
  protected readonly readOnlyAccess = computed(() => this.access()?.readOnly ?? false);
  protected readonly demoMode = computed(() => this.access()?.mode === 'demo');

  /**
   * Base tables as `schema.table`, for the pickers that need one.
   *
   * Views are excluded: neither the change feed nor a snapshot restore means anything for a view,
   * which has no rows of its own. Derived here rather than in the panels because the workbench
   * already loads the object tree for the explorer, and a panel re-fetching it would be waste.
   */
  protected readonly baseTables = computed(() =>
    this.schemas().flatMap((schema) =>
      schema.tables
        .filter((table) => table.kind !== 'VIEW')
        .map((table) => `${schema.name}.${table.name}`),
    ),
  );

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

  /** Shows a failure under a heading naming the operation that produced it. */
  private fail(title: string, message: string): void {
    this.errorTitle.set(title);
    this.error.set(message);
  }

  /** Loads the tenants the current credential can see, selecting the first as a starting point. */
  private loadTenants(): void {
    this.api
      .getAccess()
      .pipe(
        switchMap((access) => {
          this.access.set(access);
          return this.api.listTenants();
        }),
      )
      .subscribe({
        next: (tenants) => {
          this.tenants.set(tenants);
          this.error.set(null);
          const first = tenants[0];
          if (first) {
            this.firstRun.set('none');
            this.signInRejected.set(false);
            this.tenantSlug.set(first.slug);
            this.catalogName.set(first.catalogs[0]?.name ?? null);
            this.refreshCatalog();
            this.refreshHistory();
          } else {
            // The credential is accepted and there is nothing behind it. On a fresh node that is the
            // expected state, not a failure, so the panel offers to create the first workspace rather
            // than leaving two empty pickers and no explanation.
            this.firstRun.set('setup');
            this.tenantSlug.set(null);
            this.catalogName.set(null);
            this.schemas.set([]);
            this.history.set([]);
          }
        },
        error: (err: Error) => {
          // 401 is the ordinary first contact with a deployment that requires authentication, so it
          // asks for a token instead of reporting a failure the user cannot act on.
          if (err instanceof ApiError && err.status === 401) {
            this.access.set(null);
            this.firstRun.set('unauthorized');
            this.signInRejected.set(this.auth.hasToken());
            this.tenants.set([]);
            this.tenantSlug.set(null);
            this.catalogName.set(null);
            this.error.set(null);
            return;
          }

          this.firstRun.set('none');
          this.fail('Could not load workspaces', err.message);
        },
      });
  }

  /**
   * Creates the first workspace, a catalog in it, and a token that can query them.
   *
   * All three, because any two of them leave the operator stuck: a workspace with no catalog has
   * nothing to select, and a bootstrap credential provisions but deliberately cannot read — so
   * without the third step the workbench would still refuse every query it offered to run.
   */
  protected createWorkspace(request: WorkspaceRequest): void {
    const { slug, displayName, catalog } = request;
    if (this.setupBusy()) {
      return;
    }

    this.setupSlug.set(slug);
    this.setupBusy.set(true);
    this.setupError.set(null);

    // Each step tolerates "already exists" so that a retry can finish what a failed attempt began.
    // Without that, a catalog name the engine rejects strands the operator permanently: the tenant
    // was created before the failure, so every retry stops at a 409 on a step that is already done.
    this.api
      .createTenant(slug, displayName)
      .pipe(
        catchError(ignoreConflict),
        switchMap(() => this.api.createCatalog(slug, catalog).pipe(catchError(ignoreConflict))),
        switchMap(() => this.api.createToken(slug, 'workbench', 'owner')),
      )
      .subscribe({
        next: (created) => {
          this.setupBusy.set(false);
          this.issuedToken.set(created.token);
        },
        error: (err: Error) => this.failSetup(err),
      });
  }

  /** Signs in with a token pasted into the first-run panel, rather than the header popover. */
  protected signInWith(token: string): void {
    this.auth.setToken(token);
    this.loadTenants();
  }

  /** Adopts the freshly minted token as this session's credential and opens the workspace. */
  protected useIssuedToken(): void {
    const token = this.issuedToken();
    if (!token) {
      return;
    }

    this.auth.setToken(token);
    this.issuedToken.set(null);
    this.firstRun.set('none');
    this.loadTenants();
  }

  private failSetup(err: Error): void {
    this.setupBusy.set(false);
    this.setupError.set(err.message);
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

  /**
   * Points the workbench at a different catalog.
   *
   * The panels take the catalog as an input and reload themselves when it changes, so there is no
   * per-panel state to clear here — the reason the previous arrangement needed a `clearCatalogPanels`
   * method that had to be kept in step with every signal added.
   */
  protected selectCatalog(name: string): void {
    this.catalogName.set(name);
    this.refreshCatalog();
    if (this.tab() === 'snapshots') {
      this.refreshSnapshots();
    }
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
        this.fail('Query failed', err.message);
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
    const tables = this.baseTables();
    const target = tables[0] ?? 'schema.table';
    const others =
      tables.length > 1 ? `\n-- Other tables in this catalog: ${tables.slice(1).join(', ')}` : '';

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
        this.notice.set(
          `${res.operation}: ${res.detail} (${res.elapsedMilliseconds.toFixed(0)} ms)`,
        );
        this.pendingApply.set(res.dryRun ? operation : null);

        // A dry run changed nothing and needs no refresh. A committed one did, and the panel showing
        // its effect must say so: pressing Compact and watching the file count stay put is the whole
        // reason the storage panel is worth having.
        if (!res.dryRun) {
          if (this.tab() === 'snapshots') {
            this.refreshSnapshots();
          }
          this.storagePanel()?.reload();
          if (operation === 'backup') {
            this.backupsPanel()?.reload();
          }
        }
      },
      error: (err: Error) => this.fail('Maintenance failed', err.message),
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

    // A query failure belongs to the editor, not to whatever panel the operator opens next. The
    // panels carry their own banners, which are destroyed with them.
    this.error.set(null);

    if (tab === 'snapshots') {
      this.refreshSnapshots();
    }
  }

  protected replay(run: QueryRun): void {
    this.sql.set(run.sql);
    this.tab.set('results');
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
        this.fail('Could not load the catalog', err.message);
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
    this.api
      .getHistory(tenant)
      .subscribe({ next: (runs) => this.history.set(runs), error: () => undefined });
  }

  private refreshSnapshots(): void {
    const tenant = this.tenantSlug();
    const catalog = this.catalogName();
    if (!tenant || !catalog) {
      return;
    }

    this.api.getSnapshots(tenant, catalog).subscribe({
      next: (snapshots) => this.snapshots.set(snapshots),
      error: (err: Error) => this.fail('Could not load snapshots', err.message),
    });
  }
}

/**
 * Swallows a 409 so a provisioning step that has already happened does not fail the sequence.
 *
 * Only 409: every other status is a real refusal and has to reach the panel. The tenant list was
 * empty when this panel appeared, so a conflict here means an earlier attempt by this same flow got
 * further than it reported, not that someone else's workspace is being adopted.
 */
function ignoreConflict(err: unknown): Observable<unknown> {
  if (err instanceof ApiError && err.status === 409) {
    return of(null);
  }

  return throwError(() => err);
}
