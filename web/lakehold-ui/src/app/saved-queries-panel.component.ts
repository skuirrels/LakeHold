import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { Subscription } from 'rxjs';
import { LakehouseService } from './lakehouse.service';
import { SavedQuery } from './models';
import { WorkbenchQuerySource } from './workbench-query-source';

/**
 * Catalog-scoped reusable-query library.
 *
 * The editor remains the one place SQL is authored. This panel owns the control-plane metadata and
 * explicit view lifecycle, and emits execution/schema events back to the workbench instead of
 * duplicating result or catalog state.
 */
@Component({
  selector: 'lh-saved-queries-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './saved-queries-panel.component.html',
  styleUrl: './saved-queries-panel.component.css',
  host: { '[class.library]': "layout() === 'library'" },
})
export class SavedQueriesPanelComponent {
  private readonly api = inject(LakehouseService);
  private contextRequests = new Subscription();

  readonly tenant = input.required<string | null>();
  readonly catalog = input.required<string | null>();
  readonly sql = input.required<string>();
  readonly language = input('sql');
  readonly availableLanguages = input<readonly string[]>(['sql']);
  readonly languageAvailable = input(true);
  readonly readOnly = input(false);
  /** The full library adds discovery controls and metadata; the sidebar keeps its compact shape. */
  readonly layout = input<'sidebar' | 'library'>('sidebar');

  /** Loads a definition into the editor without executing it. */
  readonly openSource = output<WorkbenchQuerySource>();
  /** Requests execution with the server-selected catalog attached read-only. */
  readonly executeQuery = output<number>();
  /** A view was created, replaced, or dropped; the catalog explorer must refresh. */
  readonly schemaChanged = output<void>();

  protected readonly queries = signal<SavedQuery[]>([]);
  protected readonly loading = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);
  protected readonly search = signal('');
  protected readonly languageFilter = signal('all');
  protected readonly publicationFilter = signal<'all' | 'published' | 'attention' | 'draft'>('all');
  protected readonly sortOrder = signal<'modified-desc' | 'name-asc' | 'created-desc'>(
    'modified-desc',
  );

  protected readonly visibleQueries = computed(() => {
    const term = this.search().trim().toLowerCase();
    const language = this.languageFilter();
    const publication = this.publicationFilter();
    const filtered = this.queries().filter((query) => {
      const matchesTerm =
        !term ||
        [
          query.name,
          query.description ?? '',
          query.language ?? 'sql',
          this.ownerLabel(query),
          query.publishedSchema ?? '',
          query.publishedViewName ?? '',
        ].some((value) => value.toLowerCase().includes(term));
      const matchesLanguage = language === 'all' || (query.language ?? 'sql') === language;
      const matchesPublication =
        publication === 'all' ||
        (publication === 'published' && this.isPublishedCurrent(query)) ||
        (publication === 'attention' &&
          query.publishedViewName !== null &&
          !this.isPublishedCurrent(query)) ||
        (publication === 'draft' && query.publishedViewName === null);
      return matchesTerm && matchesLanguage && matchesPublication;
    });

    return [...filtered].sort((left, right) => {
      switch (this.sortOrder()) {
        case 'name-asc':
          return left.name.localeCompare(right.name);
        case 'created-desc':
          return Date.parse(right.createdUtc) - Date.parse(left.createdUtc);
        default:
          return Date.parse(right.updatedUtc) - Date.parse(left.updatedUtc);
      }
    });
  });

  protected readonly filterLanguages = computed(() =>
    [...new Set(this.queries().map((query) => query.language ?? 'sql'))].sort(),
  );

  protected readonly editing = signal<SavedQuery | null>(null);
  protected readonly formOpen = signal(false);
  protected readonly nameDraft = signal('');
  protected readonly descriptionDraft = signal('');

  protected readonly publishing = signal<SavedQuery | null>(null);
  protected readonly schemaDraft = signal('main');
  protected readonly viewDraft = signal('');

  protected readonly deletePending = signal<number | null>(null);
  protected readonly unpublishPending = signal<number | null>(null);

  constructor() {
    effect((onCleanup) => {
      this.tenant();
      this.catalog();
      const requests = new Subscription();
      untracked(() => {
        this.contextRequests.unsubscribe();
        this.contextRequests = requests;
        this.resetTransientState();
        this.queries.set([]);
        this.reload();
      });
      onCleanup(() => requests.unsubscribe());
    });
  }

  reload(): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    if (!tenant || !catalog) {
      this.queries.set([]);
      return;
    }

    this.loading.set(true);
    this.error.set(null);
    this.contextRequests.add(
      this.api.listSavedQueries(tenant, catalog).subscribe({
        next: (queries) => {
          this.queries.set(queries);
          this.loading.set(false);
        },
        error: (err: Error) => {
          this.error.set(err.message);
          this.loading.set(false);
        },
      }),
    );
  }

  protected beginCreate(): void {
    if (!this.languageAvailable()) {
      return;
    }

    this.editing.set(null);
    this.nameDraft.set('');
    this.descriptionDraft.set('');
    this.formOpen.set(true);
    this.error.set(null);
    this.notice.set(null);
  }

  protected beginEdit(query: SavedQuery): void {
    if (!this.isLanguageAvailable(query)) {
      return;
    }

    this.editing.set(query);
    this.nameDraft.set(query.name);
    this.descriptionDraft.set(query.description ?? '');
    this.formOpen.set(true);
    this.error.set(null);
    this.notice.set(null);
    this.openSource.emit({ language: query.language ?? 'sql', source: query.sql });
  }

  protected cancelForm(): void {
    this.formOpen.set(false);
    this.editing.set(null);
  }

  protected save(): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    const name = this.nameDraft().trim();
    const description = this.descriptionDraft().trim() || null;
    const sql = this.sql().trim();
    const language = this.language();
    if (!tenant || !catalog || !name || !sql || this.busy() || !this.languageAvailable()) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    const current = this.editing();
    const request = current
      ? this.api.updateSavedQuery(tenant, catalog, {
          id: current.id,
          revision: current.revision,
          name,
          description,
          sql,
          language,
        })
      : this.api.createSavedQuery(tenant, catalog, { name, description, sql, language });

    this.contextRequests.add(
      request.subscribe({
        next: (saved) => {
          this.upsert(saved);
          this.busy.set(false);
          this.formOpen.set(false);
          this.editing.set(null);
          this.notice.set(current ? `Saved revision ${saved.revision}.` : 'Query saved.');
        },
        error: (err: Error) => {
          this.error.set(err.message);
          this.busy.set(false);
        },
      }),
    );
  }

  protected run(query: SavedQuery): void {
    if (!this.isLanguageAvailable(query)) {
      return;
    }

    this.error.set(null);
    this.notice.set(null);
    this.openSource.emit({ language: query.language ?? 'sql', source: query.sql });
    this.executeQuery.emit(query.id);
  }

  protected open(query: SavedQuery): void {
    this.openSource.emit({ language: query.language ?? 'sql', source: query.sql });
    this.notice.set(`Loaded “${query.name}” into the editor.`);
  }

  protected beginPublish(query: SavedQuery): void {
    if (!this.isLanguageAvailable(query)) {
      return;
    }

    this.publishing.set(query);
    this.schemaDraft.set(query.publishedSchema ?? 'main');
    this.viewDraft.set(query.publishedViewName ?? toIdentifier(query.name));
    this.error.set(null);
    this.notice.set(null);
  }

  protected cancelPublish(): void {
    this.publishing.set(null);
  }

  protected publish(): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    const query = this.publishing();
    const schema = this.schemaDraft().trim();
    const view = this.viewDraft().trim();
    if (
      !tenant ||
      !catalog ||
      !query ||
      !schema ||
      !view ||
      this.busy() ||
      !this.isLanguageAvailable(query)
    ) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.contextRequests.add(
      this.api
        .publishSavedQuery(tenant, catalog, query.id, query.revision, schema, view)
        .subscribe({
          next: (saved) => {
            this.upsert(saved);
            this.busy.set(false);
            this.publishing.set(null);
            this.notice.set(`Published ${schema}.${view} at revision ${saved.revision}.`);
            this.schemaChanged.emit();
          },
          error: (err: Error) => {
            this.error.set(err.message);
            this.busy.set(false);
          },
        }),
    );
  }

  protected requestUnpublish(query: SavedQuery): void {
    this.unpublishPending.set(query.id);
    this.deletePending.set(null);
  }

  protected cancelUnpublish(): void {
    this.unpublishPending.set(null);
  }

  protected unpublish(query: SavedQuery): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    if (!tenant || !catalog || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.contextRequests.add(
      this.api.unpublishSavedQuery(tenant, catalog, query.id, query.revision).subscribe({
        next: (saved) => {
          this.upsert(saved);
          this.busy.set(false);
          this.unpublishPending.set(null);
          this.notice.set('Published view removed. The saved query is unchanged.');
          this.schemaChanged.emit();
        },
        error: (err: Error) => {
          this.error.set(err.message);
          this.busy.set(false);
        },
      }),
    );
  }

  protected requestDelete(query: SavedQuery): void {
    this.deletePending.set(query.id);
    this.unpublishPending.set(null);
  }

  protected cancelDelete(): void {
    this.deletePending.set(null);
  }

  protected delete(query: SavedQuery): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    if (!tenant || !catalog || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.contextRequests.add(
      this.api.deleteSavedQuery(tenant, catalog, query.id, query.revision).subscribe({
        next: () => {
          this.queries.update((queries) =>
            queries.filter((candidate) => candidate.id !== query.id),
          );
          this.busy.set(false);
          this.deletePending.set(null);
          this.notice.set('Saved query deleted.');
        },
        error: (err: Error) => {
          this.error.set(err.message);
          this.busy.set(false);
        },
      }),
    );
  }

  protected isPublishedCurrent(query: SavedQuery): boolean {
    return (
      query.publishedViewName !== null &&
      !query.publishedSchemaDrifted &&
      query.publishedRevision !== null &&
      query.publishedRevision === query.revision
    );
  }

  protected isLanguageAvailable(query: SavedQuery): boolean {
    return this.availableLanguages().includes(query.language ?? 'sql');
  }

  protected ownerLabel(query: SavedQuery): string {
    return query.createdByTokenId !== null
      ? `Credential #${query.createdByTokenId}`
      : 'Browser or legacy actor';
  }

  protected modifierLabel(query: SavedQuery): string {
    return query.updatedByTokenId !== null
      ? `Credential #${query.updatedByTokenId}`
      : 'Browser or legacy actor';
  }

  protected publicationStatus(query: SavedQuery): string {
    if (!query.publishedViewName) return 'Draft';
    if (this.isPublishedCurrent(query)) return 'Published';
    return query.publishedSchemaDrifted
      ? 'Needs attention · schema changed'
      : 'Needs attention · republish';
  }

  protected publicationTitle(query: SavedQuery): string {
    if (!query.publishedViewName) return 'This saved query is not published as a view';
    if (this.isPublishedCurrent(query)) return 'The view contains the current query revision';
    return query.publishedSchemaDrifted
      ? 'The catalog schema changed after this LINQ view was compiled; review and republish it'
      : 'The saved source changed after publication; republish to update the view';
  }

  protected formatDate(value: string): string {
    return new Intl.DateTimeFormat(undefined, {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(value));
  }

  private upsert(saved: SavedQuery): void {
    this.queries.update((queries) =>
      [...queries.filter((query) => query.id !== saved.id), saved].sort((a, b) =>
        a.name.localeCompare(b.name),
      ),
    );
  }

  private resetTransientState(): void {
    this.loading.set(false);
    this.busy.set(false);
    this.formOpen.set(false);
    this.editing.set(null);
    this.publishing.set(null);
    this.deletePending.set(null);
    this.unpublishPending.set(null);
    this.error.set(null);
    this.notice.set(null);
    // Discovery filters are catalog-scoped. A term or publication state that made sense in the
    // old catalog must not make the newly selected catalog appear empty.
    this.search.set('');
    this.languageFilter.set('all');
    this.publicationFilter.set('all');
  }
}

/** Produces a conservative first proposal; the server remains the identifier authority. */
function toIdentifier(name: string): string {
  const proposal = name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9_]+/g, '_')
    .replace(/^_+|_+$/g, '')
    .slice(0, 63);

  if (!proposal) {
    return 'saved_query';
  }

  return /^[a-z_]/.test(proposal) ? proposal : `q_${proposal}`.slice(0, 63);
}
