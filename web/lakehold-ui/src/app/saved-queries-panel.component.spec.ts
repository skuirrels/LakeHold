import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { Subject } from 'rxjs';
import { LakehouseService } from './lakehouse.service';
import { SavedQueriesPanelComponent } from './saved-queries-panel.component';
import { FakeLakehouseService, savedQuery } from './test-doubles';

describe('SavedQueriesPanelComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<SavedQueriesPanelComponent>;

  async function mount(readOnly = false, sql = 'SELECT 42') {
    fixture = TestBed.createComponent(SavedQueriesPanelComponent);
    fixture.componentRef.setInput('tenant', 'demo');
    fixture.componentRef.setInput('catalog', 'analytics');
    fixture.componentRef.setInput('sql', sql);
    fixture.componentRef.setInput('readOnly', readOnly);
    await fixture.whenStable();
  }

  function text(): string {
    return fixture.nativeElement.textContent ?? '';
  }

  function buttons(label: string): HTMLButtonElement[] {
    return [...fixture.nativeElement.querySelectorAll('button')].filter(
      (button) => button.textContent?.trim() === label,
    ) as HTMLButtonElement[];
  }

  beforeEach(() => {
    api = new FakeLakehouseService();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  it('lists catalog-scoped definitions and executes the persisted id', async () => {
    api.savedQueries = [savedQuery({ id: 17 })];
    await mount();

    let executed: number | undefined;
    fixture.componentInstance.executeQuery.subscribe((id) => (executed = id));
    buttons('Run')[0].click();
    await fixture.whenStable();

    expect(text()).toContain('Revenue by country');
    expect(executed).toBe(17);
  });

  it('does not offer catalog-scoped authoring before a catalog is selected', async () => {
    fixture = TestBed.createComponent(SavedQueriesPanelComponent);
    fixture.componentRef.setInput('tenant', null);
    fixture.componentRef.setInput('catalog', null);
    fixture.componentRef.setInput('sql', 'SELECT 42');
    await fixture.whenStable();

    expect(text()).toContain('Choose a workspace and catalog');
    expect(buttons('Save current')[0].disabled).toBe(true);
  });

  it('saves the current editor SQL with its metadata', async () => {
    await mount(false, 'SELECT * FROM events');
    buttons('Save current')[0].click();
    await fixture.whenStable();

    const inputs = fixture.nativeElement.querySelectorAll('.query-form input');
    (inputs[0] as HTMLInputElement).value = 'All events';
    inputs[0].dispatchEvent(new Event('input'));
    (fixture.nativeElement.querySelector('.query-form textarea') as HTMLTextAreaElement).value =
      'Reusable event feed';
    fixture.nativeElement.querySelector('.query-form textarea').dispatchEvent(new Event('input'));
    await fixture.whenStable();

    buttons('Save')[0].click();
    await fixture.whenStable();

    expect(api.lastArgs('createSavedQuery')).toEqual([
      'demo',
      'analytics',
      {
        name: 'All events',
        description: 'Reusable event feed',
        sql: 'SELECT * FROM events',
        language: 'sql',
      },
    ]);
  });

  it('loads the persisted source while editing from the editor sidebar', async () => {
    api.savedQueries = [savedQuery({ sql: 'SELECT persisted' })];
    await mount(false, 'SELECT current');

    let opened: { language: string; source: string } | undefined;
    fixture.componentInstance.openSource.subscribe((source) => (opened = source));
    buttons('Edit')[0].click();
    await fixture.whenStable();

    expect(opened).toEqual({ language: 'sql', source: 'SELECT persisted' });
    expect(buttons('Save revision')).toHaveLength(1);
  });

  it('keeps the edit form open when revising from the full-page library', async () => {
    api.savedQueries = [savedQuery({ sql: 'SELECT persisted' })];
    fixture = TestBed.createComponent(SavedQueriesPanelComponent);
    fixture.componentRef.setInput('tenant', 'demo');
    fixture.componentRef.setInput('catalog', 'analytics');
    fixture.componentRef.setInput('sql', 'SELECT revised');
    fixture.componentRef.setInput('layout', 'library');
    await fixture.whenStable();

    let opened: { language: string; source: string } | undefined;
    fixture.componentInstance.openSource.subscribe((source) => (opened = source));
    buttons('Edit')[0].click();
    await fixture.whenStable();

    expect(opened).toBeUndefined();
    expect(text()).toContain('Update saved query');
    expect(buttons('Save revision')).toHaveLength(1);
  });

  it('marks a published view stale when the definition revision moved on', async () => {
    api.savedQueries = [
      savedQuery({
        revision: 3,
        publishedSchema: 'main',
        publishedViewName: 'revenue_by_country',
        publishedRevision: 2,
      }),
    ];
    await mount();

    expect(text()).toContain('main.revenue_by_country');
    expect(text()).toContain('Needs attention · republish');
  });

  it('distinguishes catalog schema drift from an edited definition', async () => {
    api.savedQueries = [
      savedQuery({
        language: 'csharp-linq',
        publishedSchema: 'main',
        publishedViewName: 'typed_revenue',
        publishedRevision: 1,
        publishedSchemaDrifted: true,
      }),
    ];
    await mount();

    expect(text()).toContain('Needs attention · schema changed');
    expect(fixture.nativeElement.querySelector('.publication')?.getAttribute('title')).toContain(
      'catalog schema changed',
    );
  });

  it('publishes with an explicit schema and view and tells the workbench to refresh', async () => {
    api.savedQueries = [savedQuery({ id: 9, name: 'Revenue by country' })];
    await mount();

    let schemaChanges = 0;
    fixture.componentInstance.schemaChanged.subscribe(() => schemaChanges++);
    buttons('Publish')[0].click();
    await fixture.whenStable();
    buttons('Publish view')[0].click();
    await fixture.whenStable();

    expect(api.lastArgs('publishSavedQuery')).toEqual([
      'demo',
      'analytics',
      9,
      1,
      'main',
      'revenue_by_country',
    ]);
    expect(schemaChanges).toBe(1);
  });

  it('lets readers run and open definitions without showing mutation controls', async () => {
    api.savedQueries = [savedQuery()];
    await mount(true);

    expect(buttons('Run')).toHaveLength(1);
    expect(buttons('Open')).toHaveLength(1);
    expect(buttons('Save current')).toHaveLength(0);
    expect(buttons('Edit')).toHaveLength(0);
    expect(buttons('Publish')).toHaveLength(0);
    expect(buttons('Delete')).toHaveLength(0);
  });

  it('keeps definitions visible but disables planner-dependent actions when their language is unavailable', async () => {
    api.savedQueries = [savedQuery({ language: 'csharp-linq' })];
    await mount();

    expect(buttons('Run')[0].disabled).toBe(true);
    expect(buttons('Edit')[0].disabled).toBe(true);
    expect(buttons('Publish')[0].disabled).toBe(true);
    expect(buttons('Open')[0].disabled).toBe(false);

    let opened: { language: string; source: string } | undefined;
    fixture.componentInstance.openSource.subscribe((source) => (opened = source));
    buttons('Open')[0].click();
    expect(opened?.language).toBe('csharp-linq');
  });

  it('cancels requests owned by the previous catalog', async () => {
    const analytics = new Subject<ReturnType<typeof savedQuery>[]>();
    const finance = new Subject<ReturnType<typeof savedQuery>[]>();
    vi.spyOn(api, 'listSavedQueries').mockImplementation((_, catalog) =>
      catalog === 'analytics' ? analytics : finance,
    );

    await mount();
    fixture.componentRef.setInput('layout', 'library');
    await fixture.whenStable();
    const search = fixture.nativeElement.querySelector('.library-search input') as HTMLInputElement;
    search.value = 'analytics';
    search.dispatchEvent(new Event('input'));
    await fixture.whenStable();

    fixture.componentRef.setInput('catalog', 'finance');
    await fixture.whenStable();

    analytics.next([savedQuery({ name: 'Late analytics query' })]);
    finance.next([savedQuery({ name: 'Finance query' })]);
    await fixture.whenStable();

    expect(text()).toContain('Finance query');
    expect(text()).not.toContain('Late analytics query');
    expect(
      (fixture.nativeElement.querySelector('.library-search input') as HTMLInputElement).value,
    ).toBe('');
    expect(analytics.observed).toBe(false);
  });

  it('turns reusable queries into a searchable library with owner and publication health', async () => {
    api.savedQueries = [
      savedQuery({
        id: 8,
        name: 'Current revenue',
        createdByTokenId: 41,
        updatedByTokenId: 42,
        publishedSchema: 'main',
        publishedViewName: 'current_revenue',
        publishedRevision: 1,
      }),
      savedQuery({ id: 9, name: 'Customer churn', description: 'Weekly retention watch' }),
    ];
    await mount();
    fixture.componentRef.setInput('layout', 'library');
    await fixture.whenStable();

    expect(fixture.nativeElement.querySelector('h1')?.textContent).toBe('Query library');
    expect(text()).toContain('Owner Credential #41');
    expect(text()).toContain('Modified by Credential #42');
    expect(text()).toMatch(/main\.current_revenue\s+·\s+Published/);
    expect(text()).toContain('Draft');

    const search = fixture.nativeElement.querySelector('.library-search input') as HTMLInputElement;
    search.value = 'retention';
    search.dispatchEvent(new Event('input'));
    await fixture.whenStable();

    expect(text()).toContain('Customer churn');
    expect(text()).not.toContain('Current revenue');
  });
});
