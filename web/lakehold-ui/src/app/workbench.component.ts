import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { RouterLink } from '@angular/router';
import { EMPTY, forkJoin, Observable, of, throwError } from 'rxjs';
import { catchError, switchMap } from 'rxjs/operators';
import { AuthService } from './auth.service';
import { BackupsPanelComponent } from './backups-panel.component';
import { BrandMarkComponent } from './brand-mark.component';
import { CatalogExplorerComponent } from './catalog-explorer.component';
import { AddDataHubComponent } from './add-data-hub.component';
import { ChangesPanelComponent } from './changes-panel.component';
import { DataHistoryPanelComponent } from './data-history-panel.component';
import { EjectPanelComponent } from './eject-panel.component';
import {
  FirstRunComponent,
  FirstRunMode,
  SignInRequest,
  WorkspaceRequest,
} from './first-run.component';
import { formatTime } from './format';
import { ApiError, LakehouseService } from './lakehouse.service';
import {
  AccessContext,
  BrowserSession,
  DataConnector,
  MaintenanceOperation,
  QueryDiagnostic,
  QueryLanguage,
  QueryResponse,
  QueryRun,
  SavedQuery,
  Schema,
  TableReference,
  TabularImportResult,
  Tenant,
} from './models';
import { UsersComponent } from './users.component';
import { ResultGridComponent } from './result-grid.component';
import { QueryEditorComponent } from './query-editor.component';
import { SavedQueriesPanelComponent } from './saved-queries-panel.component';
import { SchedulePanelComponent } from './schedule-panel.component';
import { StoragePanelComponent } from './storage-panel.component';
import { SystemSettingsComponent } from './system-settings.component';
import { ManagedConnectorsComponent } from './managed-connectors.component';
import { ThemeToggleComponent } from './theme-toggle.component';
import { WorkbenchSearchComponent } from './workbench-search.component';
import {
  WorkbenchDestination,
  WorkbenchNavigationComponent,
} from './workbench-navigation.component';
import { WorkbenchQuerySource } from './workbench-query-source';

const STARTER_SQL = `-- Aggregate 250k rows in a few milliseconds.
SELECT
    country,
    count(*)                AS purchases,
    ROUND(sum(revenue), 2)  AS revenue
FROM events
WHERE event_type = 'purchase'
GROUP BY country
ORDER BY revenue DESC;`;

/** Kept in the display name itself so the selector, editor label, and saved queries all agree. */
const UNAVAILABLE_SUFFIX = '(unavailable)';

type BottomTab = 'results' | 'history';

/**
 * The SQL IDE.
 *
 * This component owns the chrome — workspace and catalog selectors, the Maintain menu, credential
 * popover, editor, and its Results / Query history tabs. Add data, the query library, and every
 * operational surface are focused destinations whose components own their requests and errors.
 *
 * That split is what keeps a failure from leaking between panels: a panel's banner is destroyed with
 * the panel, so a restore refusal cannot hang over the eject list. See `docs/UI.md`.
 */
@Component({
  selector: 'lh-workbench',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AddDataHubComponent,
    BackupsPanelComponent,
    BrandMarkComponent,
    CatalogExplorerComponent,
    ChangesPanelComponent,
    DataHistoryPanelComponent,
    EjectPanelComponent,
    FirstRunComponent,
    UsersComponent,
    QueryEditorComponent,
    ResultGridComponent,
    RouterLink,
    SavedQueriesPanelComponent,
    SchedulePanelComponent,
    StoragePanelComponent,
    SystemSettingsComponent,
    ManagedConnectorsComponent,
    NgTemplateOutlet,
    ThemeToggleComponent,
    WorkbenchNavigationComponent,
    WorkbenchSearchComponent,
  ],
  templateUrl: './workbench.component.html',
  styleUrl: './workbench.component.css',
})
export class WorkbenchComponent {
  private readonly api = inject(LakehouseService);
  private readonly destroyRef = inject(DestroyRef);
  private tenantRequestGeneration = 0;
  private catalogRequestGeneration = 0;
  private historyRequestGeneration = 0;
  private readonly sourceBuffers = new Map<string, string>([['sql', STARTER_SQL]]);
  protected readonly auth = inject(AuthService);
  protected readonly browserSession = signal<BrowserSession | null>(null);

  private readonly navigationToggle = viewChild<ElementRef<HTMLButtonElement>>('navigationToggle');
  private readonly productNavigation = viewChild(WorkbenchNavigationComponent);
  private readonly contextPanel = viewChild<ElementRef<HTMLElement>>('contextPanel');

  /** Whether the credential popover is open, and the token being typed into it. */
  protected readonly credentialOpen = signal(false);
  protected readonly tokenDraft = signal('');

  /**
   * Whether the next credential saved should outlive the tab. Seeded from how the current one is
   * held, so reopening the panel shows the choice already in force rather than resetting it.
   */
  protected readonly rememberCredential = signal(this.auth.persistent());

  protected readonly tenants = signal<Tenant[]>([]);
  protected readonly access = signal<AccessContext | null>(null);
  protected readonly tenantSlug = signal<string | null>(null);
  protected readonly catalogName = signal<string | null>(null);

  protected readonly schemas = signal<Schema[]>([]);
  protected readonly schemasLoading = signal(false);
  protected readonly catalogConnected = signal(false);

  protected readonly sql = signal(STARTER_SQL);
  protected readonly language = signal('sql');
  protected readonly queryLanguages = signal<QueryLanguage[]>([
    {
      id: 'sql',
      displayName: 'SQL',
      editorLanguage: 'sql',
      starterSource: STARTER_SQL,
      readOnly: false,
      supportsSavedQueries: true,
    },
  ]);
  protected readonly activeLanguage = computed(
    () =>
      this.queryLanguages().find((candidate) => candidate.id === this.language()) ??
      this.queryLanguages()[0],
  );
  protected readonly activeLanguageAvailable = computed(
    () => this.activeLanguage()?.available !== false,
  );
  /**
   * What to say about a language that cannot run. The API's reason is the useful half — a missed
   * discovery deadline and a mismatched planner key are different problems with different fixes —
   * but a language whose planner this deployment does not configure has no reason to report.
   */
  protected readonly activeLanguageUnavailableMessage = computed(() => {
    const reason = this.activeLanguage()?.unavailableReason;
    return reason ? `Planner unavailable — ${reason}` : 'Planner unavailable — source is view-only';
  });
  protected readonly activeLanguageCanSave = computed(
    () => this.activeLanguageAvailable() && (this.activeLanguage()?.supportsSavedQueries ?? false),
  );
  protected readonly availableSavedQueryLanguages = computed(() =>
    this.queryLanguages()
      .filter((candidate) => candidate.available !== false && candidate.supportsSavedQueries)
      .map((candidate) => candidate.id),
  );
  protected readonly running = signal(false);
  protected readonly result = signal<QueryResponse | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly errorTitle = signal('Query failed');
  protected readonly diagnostics = signal<QueryDiagnostic[]>([]);
  protected readonly catalogError = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);

  protected readonly history = signal<QueryRun[]>([]);
  /** A destructive operation whose dry run has completed and is awaiting confirmation. */
  protected readonly pendingApply = signal<MaintenanceOperation | null>(null);
  protected readonly tab = signal<BottomTab>('results');
  protected readonly navigationDestination = signal<WorkbenchDestination>('workbench');
  protected readonly maintenanceOpen = signal(false);
  protected readonly searchOpen = signal(false);
  protected readonly searchQueries = signal<SavedQuery[]>([]);
  protected readonly searchConnectors = signal<DataConnector[]>([]);
  protected readonly connectorKind = signal<string | null>(null);
  protected readonly navigationOpen = signal(true);
  protected readonly contextPanelOpen = signal(true);
  protected readonly compactViewport = signal(false);
  protected readonly navigationOverlayOpen = computed(
    () => this.compactViewport() && (this.navigationOpen() || this.contextPanelOpen()),
  );
  protected readonly inspectedTable = signal<TableReference | null>(null);

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

  /** The selected catalog's own record, for the storage panel's immutable placement summary. */
  protected readonly selectedCatalog = computed(
    () => this.catalogs().find((catalog) => catalog.name === this.catalogName()) ?? null,
  );

  protected readonly ready = computed(
    () => this.tenantSlug() !== null && this.catalogName() !== null,
  );

  /**
   * An instance credential on the workbench: it administers the node, holds no workspace, and so
   * has nothing to select. `loadTenants` sends it to settings, but the workbench stays reachable
   * from the navigation, and arriving at a blank editor with no explanation is the state this
   * banner exists to end.
   */
  protected readonly instanceAdminWithoutData = computed(
    () => (this.access()?.systemAdmin ?? false) && this.tenantSlug() === null,
  );

  /**
   * What an empty picker should say. "Select a workspace" is an instruction, and giving it to
   * someone who cannot follow it is worse than saying nothing — so the reason wins where there is
   * one.
   */
  protected readonly workspacePlaceholder = computed(() => {
    if (this.instanceAdminWithoutData()) {
      return 'Administration only';
    }

    return this.tenants().length > 0 ? 'Select a workspace' : 'No workspace';
  });

  protected readonly catalogPlaceholder = computed(() => {
    if (this.instanceAdminWithoutData()) {
      return 'Administration only';
    }

    if (this.tenantSlug() === null) {
      return 'Select a workspace first';
    }

    return this.catalogs().length > 0 ? 'Select a catalog' : 'No catalog in this workspace';
  });
  protected readonly readOnlyAccess = computed(() => this.access()?.readOnly ?? false);
  protected readonly demoMode = computed(() => this.access()?.mode === 'demo');

  /**
   * Whether this principal administers the workspace it belongs to, and so reaches Users.
   *
   * Taken from the API rather than derived from the role here. An owner token that is read-only or
   * narrowed to one catalog is least privilege by design — it must not be able to mint a broader
   * credential — so it holds the role without the capability, and a rail deciding this from
   * `role === 'owner'` offered it a page every request on which is refused.
   */
  protected readonly canAdminister = computed(() => this.access()?.tenantAdmin ?? false);
  protected readonly canManageConnectors = computed(
    () => this.canAdminister() && !this.readOnlyAccess(),
  );

  /** Whether an identity provider is configured, so "Sign in" can mean an actual login. */
  protected readonly ssoAvailable = computed(() => this.browserSession()?.oidcEnabled ?? false);

  /**
   * What the credential control says it is. Never "Sign in" — this control takes a machine token,
   * and the identity-provider login beside it is the thing that signs a person in.
   */
  protected readonly credentialLabel = computed(() => {
    if (this.auth.hasToken()) {
      return 'Token set';
    }

    return this.demoMode() ? 'Operator token' : 'API token';
  });

  /** Base tables for history and change-feed pickers; views own no rows or snapshots of their own. */
  protected readonly baseTableReferences = computed<TableReference[]>(() =>
    this.schemas().flatMap((schema) =>
      schema.tables
        .filter((table) => table.kind !== 'VIEW')
        .map((table) => ({ schemaName: schema.name, tableName: table.name })),
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
    this.watchNavigationBreakpoint();
    this.loadBrowserSession();
  }

  private loadBrowserSession(): void {
    this.api.getBrowserSession().subscribe({
      next: (session) => {
        this.browserSession.set(session);
        if (session.authenticated) {
          // A pasted machine credential wins because it adds an Authorization header. Remove that
          // stale override once a human session exists, then load the human's effective access.
          this.auth.clear();
        }
        this.loadTenants();
      },
      error: () => this.loadTenants(),
    });
  }

  private loadQueryLanguages(): void {
    this.api.getQueryLanguages().subscribe({
      next: (languages) => {
        if (languages.length > 0) {
          // An installed-but-unhealthy planner arrives in this list with its reason, and stays in
          // the selector. Dropping it is what left "where did C# LINQ go?" with no answer anywhere
          // the person asking could see.
          const available = languages.map((language) =>
            language.available === false
              ? markUnavailable(language)
              : { ...language, available: true },
          );
          const current = this.language();
          if (available.some((language) => language.id === current)) {
            this.queryLanguages.set(available);
            return;
          }

          const previous = this.queryLanguages().find((language) => language.id === current);
          this.queryLanguages.set([...available, unavailableLanguage(current, previous)]);
        }
      },
      error: () => undefined,
    });
  }

  /**
   * Keeps the desktop rail open by default and the compact drawer closed by default.
   *
   * The Workbench route is client-rendered, but the guard keeps this component straightforward to
   * exercise under jsdom as well. Crossing the breakpoint resets the shell to the least surprising
   * state for the new layout instead of leaving a desktop rail covering a phone-sized editor.
   */
  private watchNavigationBreakpoint(): void {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
      return;
    }

    const media = window.matchMedia('(max-width: 900px)');
    const apply = (compact: boolean): void => {
      const focusWillBeHidden =
        compact &&
        typeof document !== 'undefined' &&
        (this.productNavigation()?.contains(document.activeElement) ||
          this.contextPanel()?.nativeElement.contains(document.activeElement));

      this.compactViewport.set(compact);
      this.navigationOpen.set(!compact);
      this.contextPanelOpen.set(!compact);
      if (focusWillBeHidden) {
        this.focusNavigationToggle();
      }
    };

    apply(media.matches);
    const listener = (event: MediaQueryListEvent): void => apply(event.matches);
    media.addEventListener('change', listener);
    this.destroyRef.onDestroy(() => media.removeEventListener('change', listener));
  }

  /** Shows a failure under a heading naming the operation that produced it. */
  private fail(title: string, message: string): void {
    this.errorTitle.set(title);
    this.error.set(message);
  }

  /** Loads the tenants the current credential can see, selecting the first as a starting point. */
  private loadTenants(): void {
    const requestGeneration = ++this.tenantRequestGeneration;
    this.catalogRequestGeneration += 1;
    this.catalogConnected.set(false);
    this.catalogError.set(null);
    this.schemasLoading.set(false);
    this.api
      .getAccess()
      .pipe(
        switchMap((access) => {
          if (requestGeneration !== this.tenantRequestGeneration) {
            return EMPTY;
          }

          this.access.set(access);
          this.loadQueryLanguages();
          return this.api.listTenants();
        }),
      )
      .subscribe({
        next: (tenants) => {
          if (requestGeneration !== this.tenantRequestGeneration) {
            return;
          }

          this.tenants.set(tenants);
          this.error.set(null);
          if (this.access()?.systemAdmin && tenants.length > 0) {
            // An instance credential administers the node but cannot query tenant data. Keep its
            // administration surface reachable once the first workspace exists. An empty node must
            // still offer first-run provisioning.
            this.firstRun.set('none');
            this.signInRejected.set(false);
            this.navigationDestination.set('settings');
            this.tenantSlug.set(null);
            this.catalogName.set(null);
            this.schemas.set([]);
            this.history.set([]);
            this.catalogConnected.set(false);
            return;
          }

          // A credential can be replaced while an administration page is open, and the new one may
          // not reach it: System Settings is instance-only, and Users needs a workspace owner. Send
          // such a principal back to a surface it owns rather than leaving it on a refusal.
          const destination = this.navigationDestination();
          if (destination === 'settings' || (destination === 'users' && !this.canAdminister())) {
            this.navigationDestination.set('workbench');
          }

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
            this.catalogConnected.set(false);
          }
        },
        error: (err: Error) => {
          if (requestGeneration !== this.tenantRequestGeneration) {
            return;
          }

          // 401 is the ordinary first contact with a deployment that requires authentication, so it
          // asks for a token instead of reporting a failure the user cannot act on.
          if (err instanceof ApiError && err.status === 401) {
            this.access.set(null);
            this.firstRun.set('unauthorized');
            this.signInRejected.set(this.auth.hasToken());
            this.tenants.set([]);
            this.tenantSlug.set(null);
            this.catalogName.set(null);
            this.catalogConnected.set(false);
            this.error.set(null);
            return;
          }

          this.firstRun.set('none');
          this.catalogConnected.set(false);
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
    const { slug, displayName, catalog, placement } = request;
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
        switchMap(() =>
          this.api.createCatalog(slug, catalog, placement).pipe(catchError(ignoreConflict)),
        ),
        switchMap(() =>
          this.api.createToken(slug, {
            name: 'workbench',
            role: 'owner',
            readOnly: false,
            catalogName: null,
            expiresUtc: null,
          }),
        ),
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
  protected signInWith(request: SignInRequest): void {
    this.auth.setToken(request.token, request.persist);
    this.rememberCredential.set(request.persist);
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
    this.rememberCredential.set(this.auth.persistent());
    this.credentialOpen.update((open) => !open);
  }

  /** Stores the typed token and reloads, since it may change which tenants are visible. */
  protected saveCredential(): void {
    this.auth.setToken(this.tokenDraft(), this.rememberCredential());
    this.tokenDraft.set('');
    this.credentialOpen.set(false);
    this.loadTenants();
  }

  /** Forgets the token and reloads as an anonymous caller. */
  protected clearCredential(): void {
    this.auth.clear();
    this.tokenDraft.set('');
    this.rememberCredential.set(false);
    this.credentialOpen.set(false);
    this.loadTenants();
  }

  protected selectTenant(slug: string): void {
    this.tenantSlug.set(slug);
    this.catalogName.set(this.catalogs()[0]?.name ?? null);
    this.inspectedTable.set(null);
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
    this.inspectedTable.set(null);
    this.refreshCatalog();
    this.refreshHistory();
  }

  protected run(): void {
    const tenant = this.tenantSlug();
    const catalog = this.catalogName();
    const sql = this.sql().trim();
    const language = this.language();

    if (!tenant || !catalog || !sql || this.running() || !this.activeLanguageAvailable()) {
      return;
    }

    this.executeRequest(
      this.api.execute(tenant, catalog, sql, language),
      language === 'sql' && /^\s*(create|drop|alter)\b/i.test(sql),
    );
  }

  /** Refreshes every catalog surface invalidated by a newly imported table. */
  protected onFileImported(result: TabularImportResult): void {
    this.notice.set(
      `Imported ${result.rowsImported.toLocaleString()} rows into ${result.schema}.${result.table}` +
        (result.rejectedRows > 0
          ? `; ${result.rejectedRows.toLocaleString()} malformed rows were rejected.`
          : '.'),
    );
    this.refreshCatalog();
    this.refreshHistory();
  }

  /** Runs the persisted definition by id, so the server — not the browser — chooses the SQL. */
  protected runSavedQuery(id: number): void {
    const tenant = this.tenantSlug();
    const catalog = this.catalogName();
    if (!tenant || !catalog || this.running()) {
      return;
    }

    this.executeRequest(this.api.executeSavedQuery(tenant, catalog, id), false);
  }

  protected insertSql(snippet: string): void {
    this.switchLanguage('sql');
    this.sql.set(snippet);
  }

  protected openSource(query: WorkbenchQuerySource): void {
    if (!this.queryLanguages().some((candidate) => candidate.id === query.language)) {
      this.queryLanguages.update((languages) => [
        ...languages,
        unavailableLanguage(query.language),
      ]);
    }

    this.switchLanguage(query.language);
    this.sql.set(query.source);
    this.sourceBuffers.set(query.language, query.source);
  }

  protected switchLanguage(language: string): void {
    if (language === this.language()) {
      return;
    }

    this.sourceBuffers.set(this.language(), this.sql());
    const descriptor =
      this.queryLanguages().find((candidate) => candidate.id === language) ??
      unavailableLanguage(language);
    if (!this.queryLanguages().some((candidate) => candidate.id === language)) {
      this.queryLanguages.update((languages) => [...languages, descriptor]);
    }

    this.language.set(language);
    const buffered = this.sourceBuffers.get(language);
    const starter = buffered ?? descriptor.starterSource;
    this.sql.set(starter);
    if (buffered === undefined && descriptor.available !== false) {
      this.loadStarter(language, starter);
    }
    this.result.set(null);
    this.error.set(null);
    this.diagnostics.set([]);
  }

  private loadStarter(language: string, fallback: string): void {
    const tenant = this.tenantSlug();
    const catalog = this.catalogName();
    if (!tenant || !catalog) {
      return;
    }

    this.api.getQueryStarter(tenant, catalog, language).subscribe({
      next: (starter) => {
        if (
          this.language() === language &&
          this.sql() === fallback &&
          !this.sourceBuffers.has(language)
        ) {
          this.sql.set(starter.source);
          this.sourceBuffers.set(language, starter.source);
        }
      },
      error: () => undefined,
    });
  }

  protected toggleNavigation(): void {
    const opening = !this.navigationOpen();
    this.navigationOpen.set(opening);
    if (opening && this.compactViewport()) {
      this.contextPanelOpen.set(false);
      this.focusNavigationDestination();
    }
  }

  protected closeNavigationOverlays(): void {
    this.maintenanceOpen.set(false);
    if (!this.navigationOverlayOpen()) {
      return;
    }

    this.navigationOpen.set(false);
    this.contextPanelOpen.set(false);
    this.focusNavigationToggle();
  }

  protected toggleContextPanel(): void {
    const opening = !this.contextPanelOpen();
    this.contextPanelOpen.set(opening);

    if (!opening) {
      if (this.compactViewport() || !this.navigationOpen()) {
        this.focusNavigationToggle();
      } else {
        this.focusNavigationDestination();
      }
      return;
    }

    if (this.compactViewport()) {
      this.navigationOpen.set(false);
      this.focusContextPanel();
    }
  }

  /** Re-reads the workspace list after System Settings provisions a workspace or catalog. */
  protected refreshWorkspaces(): void {
    this.loadTenants();
  }

  protected toggleSearch(): void {
    const opening = !this.searchOpen();
    this.searchOpen.set(opening);
    if (!opening) {
      return;
    }

    // Never show metadata from the catalog that was selected when the palette was last opened.
    this.searchQueries.set([]);
    this.searchConnectors.set([]);
    const tenant = this.tenantSlug();
    const catalog = this.catalogName();
    if (!tenant || !catalog) {
      return;
    }

    forkJoin({
      queries: this.api.listSavedQueries(tenant, catalog).pipe(catchError(() => of([]))),
      connectors: this.canManageConnectors()
        ? this.api.listConnectors(tenant, catalog).pipe(catchError(() => of([])))
        : of([] as DataConnector[]),
    }).subscribe(({ queries, connectors }) => {
      if (this.tenantSlug() === tenant && this.catalogName() === catalog) {
        this.searchQueries.set(queries);
        this.searchConnectors.set(connectors);
      }
    });
  }

  protected selectSearchContext(context: { tenant: string; catalog: string }): void {
    this.tenantSlug.set(context.tenant);
    this.catalogName.set(context.catalog);
    this.inspectedTable.set(null);
    this.refreshCatalog();
    this.refreshHistory();
    this.navigationDestination.set('workbench');
  }

  protected openSearchSource(query: WorkbenchQuerySource): void {
    this.openSource(query);
    this.tab.set('results');
    this.navigationDestination.set('workbench');
  }

  protected configureConnector(kind: string): void {
    this.connectorKind.set(kind);
    this.openNavigation('connectors');
  }

  protected openNavigation(destination: WorkbenchDestination): void {
    this.navigationDestination.set(destination);
    this.maintenanceOpen.set(false);
    if (destination !== 'connectors') {
      // A source selected in Add data is a one-navigation handoff, not a sticky connector default.
      this.connectorKind.set(null);
    }
    if (destination !== 'workbench') {
      this.error.set(null);
      this.catalogError.set(null);
    }

    switch (destination) {
      case 'workbench':
        this.showTab('results', false);
        this.contextPanelOpen.set(true);
        break;
      case 'catalog':
      case 'queries':
      case 'add-data':
      case 'users':
      case 'settings':
      case 'connectors':
      case 'history':
      case 'snapshots':
      case 'storage':
      case 'changes':
      case 'backups':
      case 'ejects':
      case 'schedule':
        // Focused destinations own the canvas; only the editor keeps its catalog context panel.
        this.contextPanelOpen.set(false);
        break;
    }

    if (this.compactViewport()) {
      this.navigationOpen.set(false);
      this.focusNavigationToggle();
    }
  }

  private focusNavigationToggle(): void {
    this.afterRender(() => this.navigationToggle()?.nativeElement.focus());
  }

  private focusNavigationDestination(): void {
    this.afterRender(() => this.productNavigation()?.focusCurrentDestination());
  }

  private focusContextPanel(): void {
    this.afterRender(() =>
      this.contextPanel()
        ?.nativeElement.querySelector<HTMLButtonElement>('.context-toggle')
        ?.focus(),
    );
  }

  private afterRender(action: () => void): void {
    if (typeof window === 'undefined') {
      return;
    }

    window.setTimeout(action);
  }

  protected inspectTable(table: TableReference): void {
    this.inspectedTable.set(table);
    this.navigationDestination.set('storage');
    this.contextPanelOpen.set(false);
    this.error.set(null);
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
    this.maintenanceOpen.set(false);
    this.error.set(null);
    this.pendingApply.set(null);

    this.api.runMaintenance(tenant, catalog, operation, apply).subscribe({
      next: (res) => {
        this.notice.set(
          `${res.operation}: ${res.detail} (${res.elapsedMilliseconds.toFixed(0)} ms)`,
        );
        this.pendingApply.set(res.dryRun ? operation : null);

        // Operational destinations are created on navigation and load their own current state.
        // They are never mounted beside this menu, so there is no hidden panel instance to refresh.
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

  protected showTab(tab: BottomTab, updateNavigation = true): void {
    this.tab.set(tab);
    if (updateNavigation) {
      this.navigationDestination.set('workbench');
    }

    // A query failure belongs to the editor, not to whatever panel the operator opens next. The
    // panels carry their own banners, which are destroyed with them.
    this.error.set(null);
    this.catalogError.set(null);
  }

  protected refreshQueryHistory(): void {
    this.refreshHistory();
  }

  protected replay(run: QueryRun): void {
    this.openSource({ language: run.language ?? 'sql', source: run.sql });
    this.tab.set('results');
    this.navigationDestination.set('workbench');
  }

  protected refreshCatalog(): void {
    const tenant = this.tenantSlug();
    const catalog = this.catalogName();
    const requestGeneration = ++this.catalogRequestGeneration;
    this.catalogConnected.set(false);
    this.catalogError.set(null);
    if (!tenant || !catalog) {
      this.schemas.set([]);
      this.schemasLoading.set(false);
      return;
    }

    this.schemasLoading.set(true);
    this.api.getSchemas(tenant, catalog).subscribe({
      next: (schemas) => {
        if (
          requestGeneration !== this.catalogRequestGeneration ||
          this.tenantSlug() !== tenant ||
          this.catalogName() !== catalog
        ) {
          return;
        }

        this.schemas.set(schemas);
        this.catalogConnected.set(true);
        this.schemasLoading.set(false);
      },
      error: (err: Error) => {
        if (
          requestGeneration !== this.catalogRequestGeneration ||
          this.tenantSlug() !== tenant ||
          this.catalogName() !== catalog
        ) {
          return;
        }

        this.catalogError.set(err.message);
        this.catalogConnected.set(false);
        this.schemasLoading.set(false);
      },
    });
  }

  private executeRequest(request: Observable<QueryResponse>, refreshSchema: boolean): void {
    this.running.set(true);
    this.error.set(null);
    this.notice.set(null);
    this.tab.set('results');
    this.navigationDestination.set('workbench');

    request.subscribe({
      next: (response) => {
        this.result.set(response);
        this.diagnostics.set(response.diagnostics ?? []);
        this.running.set(false);
        this.refreshHistory();
        if (refreshSchema) {
          this.refreshCatalog();
        }
      },
      error: (err: Error) => {
        this.diagnostics.set(err instanceof ApiError ? err.diagnostics : []);
        this.fail('Query failed', err.message);
        this.result.set(null);
        this.running.set(false);
        this.refreshHistory();
      },
    });
  }

  protected refreshHistory(): void {
    const tenant = this.tenantSlug();
    const catalog = this.catalogName();
    const requestGeneration = ++this.historyRequestGeneration;
    if (!tenant || !catalog) {
      this.history.set([]);
      return;
    }

    // History is advisory; a failure here must not replace the query error the user is reading.
    this.api.getHistory(tenant).subscribe({
      next: (runs) => {
        if (
          requestGeneration === this.historyRequestGeneration &&
          this.tenantSlug() === tenant &&
          this.catalogName() === catalog
        ) {
          this.history.set(runs.filter((run) => run.catalogName === catalog));
        }
      },
      error: () => undefined,
    });
  }
}

/**
 * Marks a language the Workbench cannot plan with, keeping whatever the API or an earlier
 * discovery already told us about it so the selector still reads as the language rather than as a
 * configured id. Nothing can be planned, so nothing can be run or saved either.
 */
function markUnavailable(language: QueryLanguage): QueryLanguage {
  return {
    ...language,
    displayName: language.displayName.endsWith(UNAVAILABLE_SUFFIX)
      ? language.displayName
      : `${language.displayName} ${UNAVAILABLE_SUFFIX}`,
    readOnly: true,
    supportsSavedQueries: false,
    available: false,
    unavailableReason: language.unavailableReason ?? null,
  };
}

/** A language no configured planner claims — a saved definition outlived its plugin. */
function unavailableLanguage(language: string, previous?: QueryLanguage): QueryLanguage {
  return markUnavailable({
    id: language,
    displayName: previous?.displayName ?? language,
    editorLanguage: previous?.editorLanguage ?? 'text',
    starterSource: previous?.starterSource ?? '',
    readOnly: true,
    supportsSavedQueries: false,
    unavailableReason: previous?.unavailableReason ?? null,
  });
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
